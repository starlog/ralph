using Ralph.Models;
using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

public class VerificationRunnerTests : IDisposable
{
    private readonly string _tempDir;

    public VerificationRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ralph-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Exit_zero_command_returns_success()
    {
        var spec = new VerificationSpec { Command = OperatingSystem.IsWindows() ? "exit 0" : "true" };
        var r = await new VerificationRunner().RunAsync(spec, _tempDir);

        Assert.True(r.Success);
        Assert.Equal(0, r.ExitCode);
        Assert.False(r.TimedOut);
    }

    [Fact]
    public async Task Nonzero_exit_returns_failure_with_stderr_captured()
    {
        // POSIX: stderr로 메시지 + exit 3
        var spec = OperatingSystem.IsWindows()
            ? new VerificationSpec { Command = "echo something failed 1>&2 & exit 3" }
            : new VerificationSpec { Command = "echo 'something failed' >&2; exit 3" };

        var r = await new VerificationRunner().RunAsync(spec, _tempDir);

        Assert.False(r.Success);
        Assert.Equal(3, r.ExitCode);
        Assert.False(r.TimedOut);
        Assert.Contains("something failed", r.Stderr);
    }

    [Fact]
    public async Task Stdout_is_captured()
    {
        var spec = new VerificationSpec { Command = "echo hello-from-test" };
        var r = await new VerificationRunner().RunAsync(spec, _tempDir);

        Assert.True(r.Success);
        Assert.Contains("hello-from-test", r.Stdout);
    }

    [Fact]
    public async Task Timeout_kills_process_and_marks_timedout()
    {
        // 5초간 sleep하는 명령. timeoutSec=1로 강제 종료 검증.
        var spec = OperatingSystem.IsWindows()
            ? new VerificationSpec { Command = "ping -n 6 127.0.0.1 > nul", TimeoutSec = 1 }
            : new VerificationSpec { Command = "sleep 5", TimeoutSec = 1 };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await new VerificationRunner().RunAsync(spec, _tempDir);
        sw.Stop();

        Assert.True(r.TimedOut, $"timed out should be true. exit={r.ExitCode} duration={sw.Elapsed.TotalSeconds:F1}s");
        Assert.False(r.Success);
        // 1초 timeout인데 5초 sleep이 다 돌면 정지가 안 된 것 — 4초 안에는 종료되어야 함.
        Assert.True(sw.Elapsed.TotalSeconds < 4.0,
            $"process kill 동작 안 함. duration={sw.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public async Task Working_directory_is_honored()
    {
        // tmpDir 안에 marker 파일 생성 후 ls/dir로 보여 검증
        var marker = "ralph-verify-marker.txt";
        await File.WriteAllTextAsync(Path.Combine(_tempDir, marker), "ok");

        var spec = OperatingSystem.IsWindows()
            ? new VerificationSpec { Command = $"dir /b {marker}" }
            : new VerificationSpec { Command = $"ls {marker}" };

        var r = await new VerificationRunner().RunAsync(spec, _tempDir);
        Assert.True(r.Success);
        Assert.Contains(marker, r.Stdout);
    }

    [Fact]
    public void BuildFailureContext_includes_command_exit_and_streams()
    {
        var r = new VerificationResult(
            Success: false,
            ExitCode: 1,
            Stdout: "test 1 passed\ntest 2 failed",
            Stderr: "AssertionError at line 42",
            Duration: TimeSpan.FromSeconds(3),
            TimedOut: false);

        var ctx = VerificationRunner.BuildFailureContext("pytest tests/", r);

        Assert.Contains("pytest tests/", ctx);
        Assert.Contains("Exit code: 1", ctx);
        Assert.Contains("test 2 failed", ctx);
        Assert.Contains("AssertionError at line 42", ctx);
    }

    [Fact]
    public void BuildFailureContext_marks_timeout()
    {
        var r = new VerificationResult(
            Success: false, ExitCode: -1,
            Stdout: "", Stderr: "",
            Duration: TimeSpan.FromSeconds(120), TimedOut: true);

        var ctx = VerificationRunner.BuildFailureContext("dotnet test", r);
        Assert.Contains("TIMEOUT", ctx);
    }

    [Fact]
    public async Task Empty_command_throws()
    {
        var spec = new VerificationSpec { Command = "" };
        await Assert.ThrowsAsync<ArgumentException>(
            () => new VerificationRunner().RunAsync(spec, _tempDir));
    }
}
