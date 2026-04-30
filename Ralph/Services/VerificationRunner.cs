using System.Diagnostics;
using System.Text;
using Ralph.Models;

namespace Ralph.Services;

public sealed record VerificationResult(
    bool Success,
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration,
    bool TimedOut);

/// <summary>
/// Task 완료 후 외부 검증 명령을 실행하고 exit code로 ground truth를 결정합니다.
/// Claude self-report에 의존하지 않는 검증 게이트.
/// </summary>
public class VerificationRunner
{
    public const int DefaultTimeoutSec = 120;
    private const int MaxStreamBytes = 4000;

    public async Task<VerificationResult> RunAsync(
        VerificationSpec spec,
        string workingDirectory,
        RalphLogger? logger = null,
        TextWriter? output = null,
        CancellationToken ct = default)
    {
        if (spec is null) throw new ArgumentNullException(nameof(spec));
        if (string.IsNullOrWhiteSpace(spec.Command))
            throw new ArgumentException("verification.command is empty", nameof(spec));
        logger ??= RalphLogger.Null;

        var timeoutSec = spec.TimeoutSec is > 0 ? spec.TimeoutSec.Value : DefaultTimeoutSec;
        var psi = BuildShellPsi(spec.Command, workingDirectory);

        logger.Info($"[verification] running: {spec.Command} (cwd: {workingDirectory}, timeout: {timeoutSec}s)");
        output?.WriteLine($"\n=== Verification: {spec.Command} ===");

        var sw = Stopwatch.StartNew();
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start verification: {spec.Command}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        var timedOut = false;
        var exitTask = process.WaitForExitAsync(ct);
        var winner = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(timeoutSec), ct));
        if (winner != exitTask)
        {
            // 외부 ct fired면 사용자 cancel — OCE로 propagate. 그 외에만 timeout으로 판정.
            if (ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                try { await process.WaitForExitAsync(CancellationToken.None); } catch { /* swallow */ }
                ct.ThrowIfCancellationRequested();
            }
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            try { await exitTask; } catch { /* swallow */ }
        }

        // Stream tasks should complete shortly after process exit/kill
        string stdout, stderr;
        try { stdout = await stdoutTask; } catch { stdout = ""; }
        try { stderr = await stderrTask; } catch { stderr = ""; }
        sw.Stop();

        var exit = timedOut ? -1 : process.ExitCode;
        var success = !timedOut && exit == 0;

        if (output != null)
        {
            if (!string.IsNullOrEmpty(stdout))
            {
                output.WriteLine("--- stdout ---");
                output.WriteLine(stdout.TrimEnd());
            }
            if (!string.IsNullOrEmpty(stderr))
            {
                output.WriteLine("--- stderr ---");
                output.WriteLine(stderr.TrimEnd());
            }
            output.WriteLine(
                $"=== verification {(success ? "PASS" : "FAIL")} (exit={exit}, {sw.Elapsed.TotalSeconds:F1}s)" +
                $"{(timedOut ? " [TIMEOUT]" : "")} ===");
        }

        logger.Info(
            $"[verification] {(success ? "passed" : "failed")} exit={exit} " +
            $"duration={sw.Elapsed.TotalSeconds:F1}s timedOut={timedOut}");

        return new VerificationResult(
            Success: success,
            ExitCode: exit,
            Stdout: stdout,
            Stderr: stderr,
            Duration: sw.Elapsed,
            TimedOut: timedOut);
    }

    /// <summary>
    /// 검증 실패 결과를 Claude 다음 시도 prompt 앞에 prepend할 retry context로 포맷합니다.
    /// stdout/stderr는 각 4000자로 truncate.
    /// </summary>
    public static string BuildFailureContext(string command, VerificationResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 이전 시도 검증 실패 (외부 명령 exit code != 0)");
        sb.AppendLine();
        sb.AppendLine($"검증 명령: `{command}`");
        if (r.TimedOut)
            sb.AppendLine($"결과: TIMEOUT ({r.Duration.TotalSeconds:F0}s 초과 → 강제 종료)");
        else
            sb.AppendLine($"Exit code: {r.ExitCode}");

        if (!string.IsNullOrWhiteSpace(r.Stdout))
        {
            sb.AppendLine();
            sb.AppendLine("stdout:");
            sb.AppendLine("```");
            sb.AppendLine(Truncate(r.Stdout.TrimEnd(), MaxStreamBytes));
            sb.AppendLine("```");
        }
        if (!string.IsNullOrWhiteSpace(r.Stderr))
        {
            sb.AppendLine();
            sb.AppendLine("stderr:");
            sb.AppendLine("```");
            sb.AppendLine(Truncate(r.Stderr.TrimEnd(), MaxStreamBytes));
            sb.AppendLine("```");
        }
        sb.AppendLine();
        sb.AppendLine("위 검증 실패의 실제 원인을 먼저 분석한 뒤 코드를 수정하세요.");
        sb.AppendLine("동일한 접근의 단순 반복은 같은 실패를 유발합니다. 검증 명령은 ralph가 자동 재실행합니다.");
        return sb.ToString();
    }

    private static ProcessStartInfo BuildShellPsi(string command, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory,
        };
        if (OperatingSystem.IsWindows())
        {
            // cmd.exe /c uses CMD's own quoting rules, not CommandLineToArgvW.
            // psi.ArgumentList would escape inner `"` as `\"`, which cmd misreads
            // (cmd treats `\` as literal), splitting tokens at the wrong boundaries.
            // Build the raw command line and rely on cmd's `/C "..."` outer-quote-strip rule.
            psi.FileName = "cmd.exe";
            psi.Arguments = $"/c \"{command}\"";
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        return psi;
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "\n... (truncated)";
}
