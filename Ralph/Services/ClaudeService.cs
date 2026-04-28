using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
}

public record TokenUsage(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens)
{
    public long TotalInput => InputTokens + CacheReadTokens + CacheCreationTokens;
}

public class ClaudeService(int maxRetries = 2, int retryDelay = 5)
{
    public bool Debug { get; set; }
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
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

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
            await process.WaitForExitAsync(ct);
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
        var hasStreamDeltas = false;
        var lastDisplayedLen = 0;
        var streamSw = new Stopwatch();
        long totalChars = 0;

        // Read stdout line by line — each line is a stream-json object
        var reader = process.StandardOutput;
        while (await reader.ReadLineAsync(ct) is { } line)
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
                    logger?.Error($"Claude stream error: {errorMsg}");
                    DebugLog($"error: {errorMsg}");
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
        var stderr = await stderrTask;
        await process.WaitForExitAsync(ct);
        DebugLog($"Process exited (code: {process.ExitCode})");

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

        return new ClaudeResult
        {
            Success = process.ExitCode == 0,
            Output = finalOutput,
            Stderr = stderr,
            ErrorMessages = errorMessages.ToString(),
            ExitCode = process.ExitCode,
            Usage = capturedUsage,
            Duration = totalSw.Elapsed,
        };
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

                if (output == null)
                    AnsiConsole.MarkupLine(
                        $"[yellow]Retry attempt {attempt}/{maxRetries} (waiting {retryDelay}s)...[/]");
                logger?.Info($"Retry attempt {attempt}/{maxRetries} with failure context (exit={lastResult.ExitCode})");
                output?.WriteLine($"\n=== Retry {attempt}/{maxRetries} (previous exit={lastResult.ExitCode}) ===");
                await Task.Delay(retryDelay * 1000, ct);
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
