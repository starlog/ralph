using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Ralph.Services;

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

    public async Task<ClaudeResult> RunStreamAsync(
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
        process.Start();
        DebugLog($"Process started (PID: {process.Id})");

        // Read stderr in background to prevent deadlocks
        var stderrTask = process.StandardError.ReadToEndAsync(effectiveCt);

        // Always pipe prompt via stdin (avoids argument length limits and escaping issues)
        try
        {
            DebugLog($"Sending prompt ({prompt.Length:N0} chars)...");
            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();
            DebugLog("Prompt sent, waiting for response...");
        }
        catch (IOException ex)
        {
            // Process exited before we finished writing — read stderr for diagnostics
            var earlyStderr = await stderrTask;
            await process.WaitForExitAsync(effectiveCt);
            var errMsg = !string.IsNullOrWhiteSpace(earlyStderr) ? earlyStderr : ex.Message;
            if (output == null)
                AnsiConsole.MarkupLine($"[red]Claude process failed to start: {Markup.Escape(errMsg.Trim())}[/]");
            logger?.Error($"Claude stdin pipe broken: {errMsg.Trim()}");
            return new ClaudeResult
            {
                Success = false,
                Output = "",
                Stderr = earlyStderr,
                ExitCode = process.ExitCode,
            };
        }

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
                logger?.Warn($"Claude non-JSON output: {line}");
                DebugLog($"non-JSON: {line}");
                if (output == null)
                    AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(line)}[/]");
            }
        }

        await StopSpinner();
        streamSw.Stop();
        DebugLog($"Stream ended (totalChars: {totalChars:N0}, hasStreamDeltas: {hasStreamDeltas})");

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

            if (!timedOut) throw;

            var elapsed = totalSw.Elapsed;
            totalSw.Stop();
            logger?.Error($"Claude Code timed out after {TaskTimeoutSec}s (elapsed: {elapsed.TotalSeconds:F1}s) — process killed");
            if (output == null)
                AnsiConsole.MarkupLine(
                    $"[red]Claude Code timed out after {TaskTimeoutSec}s — process tree killed.[/]");
            else
                output.WriteLine($"\n=== TIMEOUT after {TaskTimeoutSec}s — process killed ===");

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
        };
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
                if (lastResult.RateLimited)
                {
                    delaySec = ComputeRateLimitBackoffSec(attempt, lastResult.RetryAfterSec);
                    var src = lastResult.RetryAfterSec is { } ra
                        ? $"server retry-after={ra}s"
                        : "exponential";
                    if (output == null)
                        AnsiConsole.MarkupLine(
                            $"[yellow]Rate limit 감지 — backoff {delaySec}s ({src}, jittered) 대기 (attempt {attempt}/{maxRetries})[/]");
                    logger?.Warn($"Rate-limit backoff {delaySec}s ({src}) before attempt {attempt}/{maxRetries}");
                    output?.WriteLine($"\n=== Rate-limit backoff {delaySec}s ({src}, jittered) before attempt {attempt}/{maxRetries} ===");
                }
                else
                {
                    delaySec = retryDelay;
                    if (output == null)
                        AnsiConsole.MarkupLine(
                            $"[yellow]Retry attempt {attempt}/{maxRetries} (waiting {delaySec}s)...[/]");
                    logger?.Info($"Retry attempt {attempt}/{maxRetries} with failure context (exit={lastResult.ExitCode})");
                    output?.WriteLine($"\n=== Retry {attempt}/{maxRetries} (previous exit={lastResult.ExitCode}) ===");
                }
                await Task.Delay(delaySec * 1000, ct);
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
            logger?.Error($"Claude Code failed with exit code {result.ExitCode} (attempt {attempt})");
            if (output == null)
                AnsiConsole.MarkupLine($"[red]Claude Code failed (exit code: {result.ExitCode})[/]");

            // Timeout은 retry로 잘 풀리지 않고 cost만 증가 — 즉시 종료.
            if (result.TimedOut)
            {
                logger?.Warn("Claude Code timed out — skipping further retry attempts");
                if (output == null)
                    AnsiConsole.MarkupLine("[yellow]Timeout 발생 — retry 건너뜀[/]");
                break;
            }
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
