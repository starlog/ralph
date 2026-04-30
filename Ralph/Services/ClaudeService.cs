using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Ralph.Services;

/// <summary>
/// Claude CLI 호출 실패의 분류. 재시도 정책·로그 메시지 분기에 사용한다.
/// Success == true 일 때는 의미 없음(호출자가 참조하지 않아야 한다).
/// </summary>
public enum ClaudeFailureKind
{
    /// <summary>실패 분류 미적용(Success=true 또는 분류 전 기본값).</summary>
    None = 0,
    /// <summary>claude 실행 파일을 찾지 못함. 영구 실패 — 재시도 의미 없음.</summary>
    BinaryNotFound,
    /// <summary>권한 거부(Win32 5 / errno EACCES 등). 영구 실패.</summary>
    PermissionDenied,
    /// <summary>per-attempt timeout 초과로 process tree kill.</summary>
    Timeout,
    /// <summary>rate-limit / overloaded / quota. backoff 후 재시도 가치 있음.</summary>
    RateLimited,
    /// <summary>stream-json 파싱 실패가 누적되거나 기대 메시지(assistant/result)가 전혀 안 옴.</summary>
    MalformedOutput,
    /// <summary>위 어디에도 해당하지 않는 비정상 종료. 1회만 더 시도.</summary>
    Unknown,
}

internal enum RetryAction
{
    Retry,
    Skip,
    FailFast,
}

public class ClaudeResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = "";
    public string Stderr { get; init; } = "";
    public string ErrorMessages { get; init; } = "";
    public int ExitCode { get; init; }
    public TokenUsage? Usage { get; init; }
    public TimeSpan Duration { get; init; }
    /// <summary>
    /// true이면 ClaudeService.TaskTimeoutSec를 초과해 process tree가 강제 종료된 결과.
    /// RunWithRetryAsync는 이 플래그가 true이면 retry를 건너뜁니다 (hang은 재시도로 잘 안 풀림).
    /// </summary>
    public bool TimedOut { get; init; }

    /// <summary>
    /// stderr/errorMessages에서 rate-limit/overloaded 신호를 감지한 결과. RunWithRetryAsync는
    /// 이 플래그가 true이면 일반 retryDelay 대신 jittered backoff를 적용한다 — 베이스는
    /// 서버가 준 RetryAfterSec(있으면) 또는 60s × 2^(attempt-2)(없으면), 최대 600s, jitter ×0.5~1.5.
    /// </summary>
    public bool RateLimited { get; init; }

    /// <summary>
    /// 서버가 알려준 retry-after 값(초). stream-json 에러 페이로드의 `retry_after`/`retryAfter` 필드,
    /// 또는 stderr의 `Retry-After: N` 헤더에서 추출. RunWithRetryAsync는 이 값이 있으면 추측 기반
    /// exponential backoff 대신 이 값을 backoff 베이스로 사용한다(jitter는 그대로 적용).
    /// </summary>
    public int? RetryAfterSec { get; init; }

    /// <summary>
    /// 실패 분류. Success=true 일 때는 None.
    /// 재시도 정책 분기와 사용자 진단 메시지에 사용. 기존 TimedOut/RateLimited flag와 의미상 중복되지만
    /// flag 호환을 위해 둘 다 유지한다(TimedOut=true ⇔ Timeout, RateLimited=true ⇔ RateLimited).
    /// </summary>
    public ClaudeFailureKind FailureKind { get; init; }

    /// <summary>stdin으로 전송한 prompt의 UTF-8 바이트 수. cost ledger의 promptBytes 필드에 기록된다.</summary>
    public long PromptBytes { get; init; }
}

public record TokenUsage(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens)
{
    public long TotalInput => InputTokens + CacheReadTokens + CacheCreationTokens;
}

public class ClaudeService(int maxRetries = 2, int retryDelay = 5) : IAgentRunner
{
    public bool Debug { get; set; }

    /// <summary>
    /// Claude 호출 한 번(per attempt)의 wall-clock timeout. null/0/음수면 timeout 미적용.
    /// 초과 시 process tree kill + TimedOut=true 결과 반환. 외부 ct로 인한 정상 cancel은
    /// 그대로 propagate하므로 사용자 Ctrl+C와 timeout이 구분됩니다.
    /// </summary>
    public int? TaskTimeoutSec { get; set; }

    /// <summary>테스트 전용: Task.Delay 대신 주입할 지연 함수. null이면 실제 Task.Delay 사용.</summary>
    internal Func<int, CancellationToken, Task>? DelayOverride;

    private static string BuildArgsSummary(ProcessStartInfo psi)
    {
        var args = string.Join(" ", psi.ArgumentList.Select(a =>
            a.Contains(' ') || a.Length == 0 ? $"\"{a}\"" : a));
        return $"{psi.FileName} {args}";
    }

    private static async Task RunSpinnerAsync(CancellationToken ct, Func<string> getMessage)
    {
        var frames = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
        var i = 0;
        var maxLen = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = $"  {frames[i++ % frames.Length]} {getMessage()}";
                maxLen = Math.Max(maxLen, msg.Length);
                Console.Write($"\r{msg.PadRight(maxLen)}");
                await Task.Delay(80, ct);
            }
        }
        catch (OperationCanceledException) { }
        Console.Write("\r" + new string(' ', maxLen + 2) + "\r");
    }

    public virtual async Task<ClaudeResult> RunStreamAsync(
        string prompt,
        string? model = null,
        string? workingDirectory = null,
        RalphLogger? logger = null,
        TextWriter? output = null,
        CancellationToken ct = default,
        string? allowedTools = null)
    {
        // Per-attempt timeout: 외부 ct에 추가로 CancelAfter를 적용한 linked CTS 생성.
        // 외부 ct가 fire하면 사용자 cancel(Ctrl+C) — 그대로 propagate.
        // localCts만 fire하면 timeout — process kill + TimedOut=true 결과로 graceful 반환.
        using var localCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (TaskTimeoutSec is { } sec && sec > 0)
            localCts.CancelAfter(TimeSpan.FromSeconds(sec));
        var effectiveCt = localCts.Token;

        var psi = new ProcessStartInfo
        {
            FileName = "claude",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        // Build arguments via ArgumentList (safe escaping)
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--dangerously-skip-permissions");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--include-partial-messages");
        psi.ArgumentList.Add("--verbose");

        if (model != null)
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model);

            var maxTokens = Environment.GetEnvironmentVariable("CLAUDE_CODE_MAX_OUTPUT_TOKENS") ?? "65536";
            psi.Environment["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = maxTokens;
        }

        if (allowedTools != null)
        {
            psi.ArgumentList.Add("--allowedTools");
            psi.ArgumentList.Add(allowedTools);
        }

        // Prevent "nested session" error when ralph is invoked from within Claude Code
        psi.Environment.Remove("CLAUDECODE");

        logger?.Info($"Running: {BuildArgsSummary(psi)}");
        if (!string.IsNullOrEmpty(workingDirectory))
            logger?.Info($"Working directory: {workingDirectory}");

        const int PipeBufferWarningBytes = 32 * 1024;
        var promptByteCount = (long)Encoding.UTF8.GetByteCount(prompt);

        using var process = new Process { StartInfo = psi };
        var outputBuf = new StringBuilder();
        var streamedOutput = new StringBuilder();
        var debugSw = Stopwatch.StartNew();
        var totalSw = Stopwatch.StartNew();
        TokenUsage? capturedUsage = null;

        void DebugLog(string msg)
        {
            if (Debug && output == null)
                AnsiConsole.MarkupLine($"[dim]  [[{debugSw.Elapsed:mm\\:ss\\.ff}]] {Markup.Escape(msg)}[/]");
        }

        DebugLog($"Starting claude process...");
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Process.Start 자체 실패 — claude 바이너리 부재 / 실행 권한 없음.
            // 분류 후 graceful 결과로 반환(재시도 정책에서 fail-fast 처리).
            var startKind = ex.NativeErrorCode switch
            {
                2 => ClaudeFailureKind.BinaryNotFound,    // POSIX ENOENT / Win32 ERROR_FILE_NOT_FOUND
                13 => ClaudeFailureKind.PermissionDenied, // POSIX EACCES
                5 => ClaudeFailureKind.PermissionDenied,  // Win32 ERROR_ACCESS_DENIED
                _ => ClaudeFailureKind.Unknown,
            };
            logger?.Error($"claude process start failed ({startKind}, native={ex.NativeErrorCode}): {ex.Message}");
            if (output == null)
            {
                var hint = startKind == ClaudeFailureKind.BinaryNotFound
                    ? "claude 바이너리를 찾지 못함 (PATH 확인)"
                    : startKind == ClaudeFailureKind.PermissionDenied
                        ? "claude 실행 권한 없음"
                        : $"claude 실행 실패: {ex.Message}";
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(hint)}[/]");
            }
            return new ClaudeResult
            {
                Success = false,
                Output = "",
                Stderr = ex.Message,
                ErrorMessages = ex.Message,
                ExitCode = -1,
                FailureKind = startKind,
                PromptBytes = promptByteCount,
            };
        }
        DebugLog($"Process started (PID: {process.Id})");

        // Read stderr in background to prevent deadlocks
        var stderrTask = process.StandardError.ReadToEndAsync(effectiveCt);

        // Always pipe prompt via stdin (avoids argument length limits and escaping issues).
        // Write in background (concurrent with stdout reading) to prevent deadlock when prompt
        // exceeds the OS pipe buffer — writer would block waiting for reader; reader hasn't started yet.
        if (promptByteCount > PipeBufferWarningBytes)
            logger?.Warn($"prompt 크기 {promptByteCount:N0}바이트, OS pipe buffer 초과 가능 — 청크 write 사용");
        DebugLog($"Sending prompt ({prompt.Length:N0} chars, {promptByteCount:N0} bytes)...");
        var stdinTask = WritePromptChunkedAsync(process.StandardInput, prompt, effectiveCt);

        // Show spinner while waiting for Claude's first response (console mode only, not in debug)
        var spinnerMsg = "Claude 응답 대기 중...";
        CancellationTokenSource? spinnerCts = null;
        Task? spinnerTask = null;
        if (output == null && !Debug)
        {
            spinnerCts = new CancellationTokenSource();
            spinnerTask = RunSpinnerAsync(spinnerCts.Token, () => spinnerMsg);
        }

        async Task StopSpinner()
        {
            if (spinnerCts == null) return;
            spinnerCts.Cancel();
            await spinnerTask!;
            spinnerCts.Dispose();
            spinnerCts = null;
        }

        // Determine where streaming chunks go: log file or console
        var sink = output ?? Console.Out;
        var errorMessages = new StringBuilder();
        int? capturedRetryAfter = null;
        var hasStreamDeltas = false;
        var lastDisplayedLen = 0;
        var streamSw = new Stopwatch();
        long totalChars = 0;
        var jsonParseFailures = 0;
        var gotAnyAssistantMessage = false;

        // stderr/exit는 try 바깥에서 catch가 접근할 수 있도록 미리 선언.
        string stderr = "";

        // Read stdout line by line — each line is a stream-json object
        var reader = process.StandardOutput;
        try
        {
        while (await reader.ReadLineAsync(effectiveCt) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeProp))
                    continue;

                var type = typeProp.GetString();

                if (type == "error")
                {
                    // Handle error messages from Claude Code stream-json
                    var errorMsg = root.TryGetProperty("error", out var errObj)
                        ? (errObj.TryGetProperty("message", out var em) ? em.GetString() : errObj.GetString())
                        : root.TryGetProperty("message", out var m) ? m.GetString()
                        : line;
                    errorMessages.AppendLine(errorMsg);

                    // Server-provided retry-after를 우선 캡처 (error 객체 또는 root에서)
                    if (capturedRetryAfter == null)
                        capturedRetryAfter = ReadRetryAfterFromError(root);

                    logger?.Error($"Claude stream error: {errorMsg}");
                    DebugLog($"error: {errorMsg}{(capturedRetryAfter is { } ra ? $" (retry-after={ra}s)" : "")}");
                    if (output == null)
                    {
                        await StopSpinner();
                        AnsiConsole.MarkupLine($"[red]Claude error: {Markup.Escape(errorMsg ?? line)}[/]");
                    }
                }
                else if (type == "stream_event" && root.TryGetProperty("event", out var evt))
                {
                    var eventType = evt.TryGetProperty("type", out var et) ? et.GetString() : null;
                    DebugLog($"stream_event: {eventType}");

                    if (eventType == "content_block_start")
                    {
                        if (output != null) sink.WriteLine();
                    }
                    else if (eventType == "content_block_delta"
                             && evt.TryGetProperty("delta", out var delta)
                             && delta.TryGetProperty("text", out var text))
                    {
                        var chunk = text.GetString() ?? "";
                        hasStreamDeltas = true;
                        if (!streamSw.IsRunning)
                        {
                            streamSw.Start();
                            await StopSpinner();
                            DebugLog("First content chunk received");
                        }
                        totalChars += chunk.Length;
                        streamedOutput.Append(chunk);
                        sink.Write(chunk);
                    }
                }
                else if (type == "assistant" && root.TryGetProperty("message", out var msg))
                {
                    DebugLog($"assistant message (partial update)");
                    gotAnyAssistantMessage = true;
                    if (msg.TryGetProperty("content", out var content))
                    {
                        // Clear and rebuild to handle partial message updates
                        outputBuf.Clear();
                        var partialText = new StringBuilder();
                        foreach (var item in content.EnumerateArray())
                        {
                            if (item.TryGetProperty("text", out var txt))
                            {
                                var t = txt.GetString() ?? "";
                                outputBuf.AppendLine(t);
                                partialText.Append(t);
                            }
                        }

                        // Fallback: display from partial messages when stream_event deltas aren't flowing
                        if (!hasStreamDeltas)
                        {
                            var pText = partialText.ToString();
                            if (pText.Length > lastDisplayedLen)
                            {
                                if (!streamSw.IsRunning)
                                {
                                    streamSw.Start();
                                    await StopSpinner();
                                    DebugLog("First content via fallback path");
                                }
                                var newPart = pText[lastDisplayedLen..];
                                totalChars += newPart.Length;
                                streamedOutput.Append(newPart);
                                sink.Write(newPart);
                                lastDisplayedLen = pText.Length;
                            }
                        }
                    }
                }
                else if (type == "result")
                {
                    DebugLog("result message received");
                    gotAnyAssistantMessage = true;
                    if (root.TryGetProperty("result", out var resultText))
                    {
                        var resultStr = resultText.GetString();
                        if (!string.IsNullOrWhiteSpace(resultStr) && outputBuf.Length == 0)
                            outputBuf.Append(resultStr);
                    }

                    // Token usage 파싱 (Claude Code stream-json의 result 메시지에 포함)
                    if (root.TryGetProperty("usage", out var usageObj)
                        && usageObj.ValueKind == JsonValueKind.Object)
                    {
                        long inTok = usageObj.TryGetProperty("input_tokens", out var it)
                            && it.ValueKind == JsonValueKind.Number ? it.GetInt64() : 0;
                        long outTok = usageObj.TryGetProperty("output_tokens", out var ot)
                            && ot.ValueKind == JsonValueKind.Number ? ot.GetInt64() : 0;
                        long cacheRead = usageObj.TryGetProperty("cache_read_input_tokens", out var cr)
                            && cr.ValueKind == JsonValueKind.Number ? cr.GetInt64() : 0;
                        long cacheCreate = usageObj.TryGetProperty("cache_creation_input_tokens", out var cc)
                            && cc.ValueKind == JsonValueKind.Number ? cc.GetInt64() : 0;
                        capturedUsage = new TokenUsage(inTok, outTok, cacheRead, cacheCreate);
                        DebugLog($"usage: in={inTok} out={outTok} cacheR={cacheRead} cacheC={cacheCreate}");
                    }
                }
                else
                {
                    DebugLog($"event: {type}");
                }
            }
            catch (JsonException)
            {
                // Non-JSON line — log and display for diagnostics
                jsonParseFailures++;
                logger?.Warn($"Claude non-JSON output: {line}");
                DebugLog($"non-JSON: {line}");
                if (output == null)
                    AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(line)}[/]");
            }
        }

        await StopSpinner();
        streamSw.Stop();
        DebugLog($"Stream ended (totalChars: {totalChars:N0}, hasStreamDeltas: {hasStreamDeltas})");

        // Await stdin write task (started concurrently to prevent large-prompt deadlock)
        IOException? stdinIoEx = null;
        try { await stdinTask; }
        catch (IOException ex) { stdinIoEx = ex; }
        catch (OperationCanceledException) { /* handled by outer catch below */ }

        if (stdinIoEx != null)
        {
            // Process exited before stdin was fully read — read stderr for diagnostics
            stderr = await stderrTask;
            await process.WaitForExitAsync(effectiveCt);
            var errMsg = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdinIoEx.Message;
            if (output == null)
                AnsiConsole.MarkupLine($"[red]Claude process failed to start: {Markup.Escape(errMsg.Trim())}[/]");
            logger?.Error($"Claude stdin pipe broken: {errMsg.Trim()}");
            var pipeKind = ClassifyFailure(
                exitCode: process.ExitCode,
                timedOut: false,
                rateLimited: false,
                stderr: stderr,
                errorMessages: stdinIoEx.Message,
                jsonParseFailures: 0,
                gotAnyAssistantMessage: false);
            logger?.Info($"[ClaudeClassify] kind={pipeKind} exit={process.ExitCode} jsonFails=0 gotMsg=False");
            return new ClaudeResult
            {
                Success = false,
                Output = "",
                Stderr = stderr,
                ErrorMessages = stdinIoEx.Message,
                ExitCode = process.ExitCode,
                FailureKind = pipeKind,
                PromptBytes = promptByteCount,
            };
        }

        // Drain stderr
        stderr = await stderrTask;
        await process.WaitForExitAsync(effectiveCt);
        DebugLog($"Process exited (code: {process.ExitCode})");
        }
        catch (OperationCanceledException)
        {
            // 외부 ct가 fired면 사용자 cancel — propagate. localCts만 fired면 timeout.
            await StopSpinner();
            var timedOut = !ct.IsCancellationRequested && localCts.IsCancellationRequested;

            // process tree 강제 종료 (claude 자식 프로세스까지)
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            // stdin task 관찰 (process kill 후 pipe broken 예외를 silent-consume)
            try { await stdinTask.ConfigureAwait(false); } catch { }

            if (!timedOut) throw;

            var elapsed = totalSw.Elapsed;
            totalSw.Stop();
            logger?.Error($"Claude Code timed out after {TaskTimeoutSec}s (elapsed: {elapsed.TotalSeconds:F1}s) — process killed");
            if (output == null)
                AnsiConsole.MarkupLine(
                    $"[red]Claude Code timed out after {TaskTimeoutSec}s — process tree killed.[/]");
            else
                output.WriteLine($"\n=== TIMEOUT after {TaskTimeoutSec}s — process killed ===");

            logger?.Info($"[ClaudeClassify] kind={ClaudeFailureKind.Timeout} exit=-1 jsonFails={jsonParseFailures} gotMsg={gotAnyAssistantMessage}");
            return new ClaudeResult
            {
                Success = false,
                Output = outputBuf.Length > 0 ? outputBuf.ToString() : streamedOutput.ToString(),
                Stderr = "",
                ErrorMessages = $"Claude Code timed out after {TaskTimeoutSec}s",
                ExitCode = -1,
                Usage = capturedUsage,
                Duration = elapsed,
                TimedOut = true,
                FailureKind = ClaudeFailureKind.Timeout,
                PromptBytes = promptByteCount,
            };
        }

        // Display throughput summary and final newline
        sink.WriteLine();
        if (output == null && totalChars > 0 && streamSw.ElapsedMilliseconds > 0)
        {
            var secs = streamSw.Elapsed.TotalSeconds;
            var cps = totalChars / secs;
            AnsiConsole.MarkupLine($"[dim]  {totalChars:N0} chars in {secs:F1}s ({cps:F0} chars/s)[/]");
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            if (output == null)
                AnsiConsole.MarkupLine($"[yellow]Claude stderr: {Markup.Escape(stderr.Trim())}[/]");
            logger?.Error($"Claude stderr: {stderr.Trim()}");
        }

        if (process.ExitCode != 0)
        {
            logger?.Error($"Claude exited with code {process.ExitCode}");
            if (output == null && errorMessages.Length == 0 && string.IsNullOrWhiteSpace(stderr))
                AnsiConsole.MarkupLine($"[red]Claude exited with code {process.ExitCode} (no error details available)[/]");
        }

        // Use assistant/result message if available, otherwise fall back to streamed deltas
        var finalOutput = outputBuf.Length > 0 ? outputBuf.ToString() : streamedOutput.ToString();

        totalSw.Stop();

        var errMsgsText = errorMessages.ToString();
        var rateLimited = process.ExitCode != 0 && IsRateLimitSignal(stderr, errMsgsText);
        // JSON에서 못 뽑았으면 stderr/error 텍스트에서 한 번 더 시도 (HTTP `Retry-After: N` 헤더 등).
        var retryAfter = capturedRetryAfter ?? (rateLimited ? ExtractRetryAfterSeconds(stderr, errMsgsText) : null);
        var failureKind = ClassifyFailure(
            exitCode: process.ExitCode,
            timedOut: false,
            rateLimited: rateLimited,
            stderr: stderr,
            errorMessages: errMsgsText,
            jsonParseFailures: jsonParseFailures,
            gotAnyAssistantMessage: gotAnyAssistantMessage);
        logger?.Info($"[ClaudeClassify] kind={failureKind} exit={process.ExitCode} jsonFails={jsonParseFailures} gotMsg={gotAnyAssistantMessage}");
        return new ClaudeResult
        {
            Success = process.ExitCode == 0,
            Output = finalOutput,
            Stderr = stderr,
            ErrorMessages = errMsgsText,
            ExitCode = process.ExitCode,
            Usage = capturedUsage,
            Duration = totalSw.Elapsed,
            RateLimited = rateLimited,
            RetryAfterSec = retryAfter,
            FailureKind = failureKind,
            PromptBytes = promptByteCount,
        };
    }

    /// <summary>
    /// prompt를 32KB 청크 단위로 stdin에 비동기 write+flush 후 스트림을 닫습니다.
    /// 백그라운드 태스크로 실행해 stdout 읽기와 동시에 진행함으로써 대형 prompt 데드락을 방지합니다.
    /// </summary>
    private static async Task WritePromptChunkedAsync(
        StreamWriter writer, string prompt, CancellationToken ct)
    {
        const int ChunkSize = 32 * 1024;
        var offset = 0;
        while (offset < prompt.Length)
        {
            var length = Math.Min(ChunkSize, prompt.Length - offset);
            await writer.WriteAsync(prompt.AsMemory(offset, length), ct);
            await writer.FlushAsync(ct);
            offset += length;
        }
        writer.Close();
    }

    /// <summary>
    /// stderr/error 메시지에 rate-limit/overloaded 신호가 있는지 휴리스틱으로 감지.
    /// HTTP 429, "rate limit"/"rate_limit"/"too many requests"/"overloaded"/"resource_exhausted" 패턴.
    /// false positive 시에는 단순히 backoff가 길어지는 정도의 영향만 있다.
    /// </summary>
    internal static bool IsRateLimitSignal(string stderr, string errorMessages)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(errorMessages)) return false;
        var combined = (stderr + "\n" + errorMessages).ToLowerInvariant();
        return combined.Contains("rate limit")
            || combined.Contains("rate_limit")
            || combined.Contains("too many requests")
            || combined.Contains("\"status\":429") || combined.Contains("status code 429")
            || combined.Contains(" 429 ") || combined.Contains("http 429")
            || combined.Contains("overloaded")
            || combined.Contains("resource_exhausted")
            || combined.Contains("quota exceeded");
    }

    /// <summary>
    /// 단일 시도의 실패 결과를 ClaudeFailureKind로 분류. 우선순위가 중요:
    /// (1) Success → None, (2) Timeout, (3) RateLimited(서버 신호), (4) BinaryNotFound,
    /// (5) PermissionDenied, (6) MalformedOutput, (7) Unknown.
    /// Timeout이 RateLimited 텍스트 우선이며, RateLimited 신호는 "denied" 류 텍스트 false-positive보다 우선.
    /// </summary>
    internal static ClaudeFailureKind ClassifyFailure(
        int exitCode,
        bool timedOut,
        bool rateLimited,
        string stderr,
        string errorMessages,
        int jsonParseFailures,
        bool gotAnyAssistantMessage)
    {
        if (exitCode == 0 && !timedOut) return ClaudeFailureKind.None;
        if (timedOut) return ClaudeFailureKind.Timeout;
        if (rateLimited) return ClaudeFailureKind.RateLimited;

        var combined = ((stderr ?? "") + "\n" + (errorMessages ?? "")).ToLowerInvariant();
        if (exitCode == 127
            || combined.Contains("command not found")
            || combined.Contains("no such file or directory")
            || combined.Contains("is not recognized as an internal or external"))
            return ClaudeFailureKind.BinaryNotFound;
        if (exitCode == 126
            || combined.Contains("permission denied")
            || combined.Contains("access is denied")
            || combined.Contains("operation not permitted"))
            return ClaudeFailureKind.PermissionDenied;

        if (jsonParseFailures >= 1 && !gotAnyAssistantMessage)
            return ClaudeFailureKind.MalformedOutput;

        return ClaudeFailureKind.Unknown;
    }

    /// <summary>
    /// 분류된 실패에 대한 재시도 결정. 순수 함수(테스트 가능).
    /// - BinaryNotFound/PermissionDenied: 즉시 fail-fast.
    /// - Timeout/RateLimited: maxRetries까지 재시도.
    /// - MalformedOutput/Unknown: 1회만 재시도(attempt 2부터 skip).
    /// </summary>
    internal static RetryAction DecideRetryAction(ClaudeFailureKind kind, int attemptJustFailed, int maxRetries)
    {
        return kind switch
        {
            ClaudeFailureKind.BinaryNotFound or ClaudeFailureKind.PermissionDenied
                => RetryAction.FailFast,
            ClaudeFailureKind.MalformedOutput or ClaudeFailureKind.Unknown
                => attemptJustFailed >= 2 ? RetryAction.Skip : RetryAction.Retry,
            ClaudeFailureKind.Timeout or ClaudeFailureKind.RateLimited
                => attemptJustFailed >= maxRetries ? RetryAction.Skip : RetryAction.Retry,
            _ => RetryAction.Skip,
        };
    }

    /// <summary>
    /// stream-json `error` 메시지의 `error.retry_after`/`error.retryAfter` 또는 root의 같은 필드에서
    /// 서버가 알려준 retry-after(초)를 추출. 음수/0/말도 안 되는 큰 값(>1일)은 거부.
    /// </summary>
    internal static int? ReadRetryAfterFromError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("error", out var errObj) && errObj.ValueKind == JsonValueKind.Object)
        {
            if (TryReadRetryAfter(errObj, out var s)) return s;
        }
        return TryReadRetryAfter(root, out var s2) ? s2 : null;
    }

    private static bool TryReadRetryAfter(JsonElement el, out int seconds)
    {
        seconds = 0;
        foreach (var name in new[] { "retry_after", "retryAfter", "retry-after" })
        {
            if (!el.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && d > 0 && d < 86400)
            {
                seconds = (int)Math.Ceiling(d);
                return true;
            }
            if (v.ValueKind == JsonValueKind.String
                && int.TryParse(v.GetString(), out var i) && i > 0 && i < 86400)
            {
                seconds = i;
                return true;
            }
        }
        return false;
    }

    private static readonly Regex RetryAfterJsonRegex = new(
        @"""retry[_-]?after""\s*:\s*(\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RetryAfterHeaderRegex = new(
        @"retry-after\s*:\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// stderr/error 텍스트에서 retry-after 값을 휴리스틱으로 추출(JSON에서 못 뽑은 경우 fallback).
    /// `"retry_after": N` JSON 단편, HTTP `Retry-After: N` 헤더 형식을 인식. 0/음수/하루 이상은 거부.
    /// </summary>
    internal static int? ExtractRetryAfterSeconds(string stderr, string errorMessages)
    {
        var combined = (stderr ?? "") + "\n" + (errorMessages ?? "");
        if (string.IsNullOrWhiteSpace(combined)) return null;

        var m = RetryAfterJsonRegex.Match(combined);
        if (m.Success && double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d)
            && d > 0 && d < 86400)
            return (int)Math.Ceiling(d);

        m = RetryAfterHeaderRegex.Match(combined);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var i) && i > 0 && i < 86400)
            return i;

        return null;
    }

    /// <summary>
    /// Rate-limit backoff 시간을 계산. 베이스는 서버가 준 retry-after가 있으면 그 값(최대 600s),
    /// 없으면 기존 exponential 60s × 2^(attempt-2). 베이스에 random(0.5,1.5) jitter를 곱해 동기화된
    /// herd 재진입을 흩트린다. 최종 결과는 [1, 600]초로 클램프.
    /// </summary>
    /// <param name="rng">[0,1) 난수 공급자(테스트용). null이면 Random.Shared.NextDouble.</param>
    internal static int ComputeRateLimitBackoffSec(int attempt, int? retryAfterSec, Func<double>? rng = null)
    {
        var jitter01 = (rng ?? Random.Shared.NextDouble)();
        if (jitter01 < 0) jitter01 = 0;
        if (jitter01 >= 1) jitter01 = 0.999999;
        var jitterMul = 0.5 + jitter01;

        int baseSec = retryAfterSec is { } ra && ra > 0
            ? Math.Min(600, ra)
            : Math.Min(600, 60 * (int)Math.Pow(2, Math.Max(0, attempt - 2)));

        var jittered = (int)Math.Round(baseSec * jitterMul);
        return Math.Clamp(jittered, 1, 600);
    }

    public async Task<ClaudeResult> RunWithRetryAsync(
        string prompt,
        string? model = null,
        string? workingDirectory = null,
        RalphLogger? logger = null,
        TextWriter? output = null,
        CancellationToken ct = default,
        Func<ClaudeResult, string?>? buildRetryContext = null,
        string? allowedTools = null)
    {
        var currentPrompt = prompt;
        ClaudeResult? lastResult = null;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            if (attempt > 1 && lastResult != null)
            {
                // 이전 실패 컨텍스트를 다음 시도 prompt에 prepend
                var retryContext = buildRetryContext?.Invoke(lastResult) ?? DefaultFailureContext(lastResult);
                currentPrompt = $"""
                    {retryContext}

                    ---

                    {prompt}
                    """;

                // rate-limit 신호면 backoff. 서버가 retry-after를 줬으면 그 값을 베이스로, 없으면
                // exponential(60·120·240..., 최대 600초). jitter(×0.5~1.5)로 동기화 herd 방지.
                int delaySec;
                string backoffSource;
                var prevKind = lastResult.FailureKind == ClaudeFailureKind.None
                    ? (lastResult.RateLimited ? ClaudeFailureKind.RateLimited
                        : lastResult.TimedOut ? ClaudeFailureKind.Timeout
                        : ClaudeFailureKind.Unknown)
                    : lastResult.FailureKind;
                if (lastResult.RateLimited)
                {
                    delaySec = ComputeRateLimitBackoffSec(attempt, lastResult.RetryAfterSec);
                    var hasServerHint = lastResult.RetryAfterSec is { };
                    var src = hasServerHint
                        ? $"server retry-after={lastResult.RetryAfterSec}s"
                        : "exponential";
                    backoffSource = hasServerHint ? "server-retry-after" : "exponential";
                    if (output == null)
                        AnsiConsole.MarkupLine(
                            $"[yellow]Rate limit 감지 — backoff {delaySec}s ({src}, jittered) 대기 (attempt {attempt}/{maxRetries})[/]");
                    logger?.Warn($"Rate-limit backoff {delaySec}s ({src}) before attempt {attempt}/{maxRetries}");
                    output?.WriteLine($"\n=== Rate-limit backoff {delaySec}s ({src}, jittered) before attempt {attempt}/{maxRetries} ===");
                }
                else
                {
                    delaySec = retryDelay;
                    backoffSource = "retryDelay";
                    if (output == null)
                        AnsiConsole.MarkupLine(
                            $"[yellow]Retry attempt {attempt}/{maxRetries} (waiting {delaySec}s)...[/]");
                    logger?.Info($"Retry attempt {attempt}/{maxRetries} with failure context (exit={lastResult.ExitCode})");
                    output?.WriteLine($"\n=== Retry {attempt}/{maxRetries} (previous exit={lastResult.ExitCode}) ===");
                }
                logger?.Info($"분류={prevKind}, backoff={delaySec}초({backoffSource}), 시도{attempt}/{maxRetries}");
                var delayMs = delaySec * 1000;
                await (DelayOverride?.Invoke(delayMs, ct) ?? Task.Delay(delayMs, ct));
            }
            else
            {
                logger?.Info($"Running Claude Code (attempt {attempt})");
            }

            var result = await RunStreamAsync(currentPrompt, model, workingDirectory, logger, output, ct, allowedTools);
            if (result.Success)
            {
                logger?.Info("Claude Code execution successful");
                return result;
            }

            lastResult = result;
            var kind = result.FailureKind == ClaudeFailureKind.None
                ? ClaudeFailureKind.Unknown
                : result.FailureKind;
            logger?.Error($"Claude Code failed with exit code {result.ExitCode} (attempt {attempt})");
            logger?.Warn($"[ClaudeFailure] kind={kind} exit={result.ExitCode} timedOut={result.TimedOut} rateLimited={result.RateLimited}");
            if (output == null)
            {
                var consoleHint = kind switch
                {
                    ClaudeFailureKind.BinaryNotFound => "claude 바이너리를 찾지 못함 (PATH 확인)",
                    ClaudeFailureKind.PermissionDenied => "claude 실행 권한 없음",
                    ClaudeFailureKind.Timeout => "Claude 호출 timeout (process killed)",
                    ClaudeFailureKind.MalformedOutput => "Claude 출력 파싱 실패 (JSON 깨짐)",
                    ClaudeFailureKind.RateLimited => $"Claude rate-limit 감지 (exit={result.ExitCode})",
                    _ => $"Claude 실패 (exit={result.ExitCode})",
                };
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(consoleHint)}[/]");
            }

            var action = DecideRetryAction(kind, attempt, maxRetries);
            if (action == RetryAction.FailFast)
            {
                var msg = $"Claude {kind} — fail-fast (재시도 의미 없음)";
                logger?.Error($"[ClaudeRetry] kind={kind} attempt={attempt}/{maxRetries} action=fail-fast");
                if (output == null)
                    AnsiConsole.MarkupLine($"[red]{Markup.Escape(msg)}[/]");
                else
                    output.WriteLine($"\n=== {msg} ===");
                return result;
            }

            if (action == RetryAction.Skip)
            {
                logger?.Warn($"[ClaudeRetry] kind={kind} attempt={attempt}/{maxRetries} action=skip");
                if (kind == ClaudeFailureKind.MalformedOutput || kind == ClaudeFailureKind.Unknown)
                {
                    var msg = $"{kind} 재시도 1회 소진 — 중단";
                    if (output == null)
                        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(msg)}[/]");
                    else
                        output.WriteLine($"\n=== {msg} ===");
                }
                break;
            }

            // action == Retry: 다음 iteration의 attempt > 1 분기에서 실제 backoff/log가 찍힌다.
            logger?.Info($"[ClaudeRetry] kind={kind} attempt={attempt}/{maxRetries} action=retry");
        }

        logger?.Error($"Claude Code failed after {maxRetries} attempts");
        if (output == null)
            AnsiConsole.MarkupLine($"[red]Claude Code failed after {maxRetries} attempts[/]");
        return lastResult ?? new ClaudeResult { Success = false, ExitCode = 1 };
    }

    private static string DefaultFailureContext(ClaudeResult lastResult)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 이전 시도 실패 보고");
        sb.AppendLine();
        sb.AppendLine($"Exit code: {lastResult.ExitCode}");

        if (!string.IsNullOrWhiteSpace(lastResult.Stderr))
        {
            sb.AppendLine();
            sb.AppendLine("Stderr:");
            sb.AppendLine("```");
            sb.AppendLine(lastResult.Stderr.Trim());
            sb.AppendLine("```");
        }

        if (!string.IsNullOrWhiteSpace(lastResult.ErrorMessages))
        {
            sb.AppendLine();
            sb.AppendLine("Error messages:");
            sb.AppendLine("```");
            sb.AppendLine(lastResult.ErrorMessages.Trim());
            sb.AppendLine("```");
        }

        sb.AppendLine();
        sb.AppendLine("위 실패 원인을 먼저 분석한 뒤 다른 접근으로 시도하세요. 동일한 방법을 단순 반복하면 같은 실패를 유발합니다.");
        return sb.ToString();
    }
}
