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
    public int ExitCode { get; init; }
}

public class ClaudeService(int maxRetries = 2, int retryDelay = 5)
{
    private static string BuildArgsSummary(ProcessStartInfo psi)
    {
        var args = string.Join(" ", psi.ArgumentList.Select(a =>
            a.Contains(' ') || a.Length == 0 ? $"\"{a}\"" : a));
        return $"{psi.FileName} {args}";
    }

    public async Task<ClaudeResult> RunStreamAsync(
        string prompt,
        bool noTools = false,
        string? workingDirectory = null,
        RalphLogger? logger = null,
        TextWriter? output = null,
        CancellationToken ct = default)
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
        psi.ArgumentList.Add("--verbose");

        if (noTools)
        {
            psi.ArgumentList.Add("--allowedTools");
            psi.ArgumentList.Add("none");
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add("sonnet");

            var maxTokens = Environment.GetEnvironmentVariable("CLAUDE_CODE_MAX_OUTPUT_TOKENS") ?? "65536";
            psi.Environment["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = maxTokens;
        }

        // Prevent "nested session" error when ralph is invoked from within Claude Code
        psi.Environment.Remove("CLAUDECODE");

        logger?.Info($"Running: {BuildArgsSummary(psi)}");
        if (!string.IsNullOrEmpty(workingDirectory))
            logger?.Info($"Working directory: {workingDirectory}");

        using var process = new Process { StartInfo = psi };
        var outputBuf = new StringBuilder();
        var streamedOutput = new StringBuilder();

        process.Start();

        // Read stderr in background to prevent deadlocks
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        // Always pipe prompt via stdin (avoids argument length limits and escaping issues)
        try
        {
            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();
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

        // Determine where streaming chunks go: log file or console
        var sink = output ?? Console.Out;
        var errorMessages = new StringBuilder();

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
                    if (output == null)
                        AnsiConsole.MarkupLine($"[red]Claude error: {Markup.Escape(errorMsg ?? line)}[/]");
                }
                else if (type == "stream_event" && root.TryGetProperty("event", out var evt))
                {
                    var eventType = evt.TryGetProperty("type", out var et) ? et.GetString() : null;

                    if (eventType == "content_block_start")
                    {
                        sink.WriteLine();
                    }
                    else if (eventType == "content_block_delta"
                             && evt.TryGetProperty("delta", out var delta)
                             && delta.TryGetProperty("text", out var text))
                    {
                        var chunk = text.GetString() ?? "";
                        sink.Write(chunk);
                        streamedOutput.Append(chunk);
                    }
                }
                else if (type == "assistant" && root.TryGetProperty("message", out var msg))
                {
                    if (msg.TryGetProperty("content", out var content))
                    {
                        // Clear and rebuild to handle partial message updates
                        outputBuf.Clear();
                        foreach (var item in content.EnumerateArray())
                        {
                            if (item.TryGetProperty("text", out var txt))
                                outputBuf.AppendLine(txt.GetString());
                        }
                    }
                }
                else if (type == "result" && root.TryGetProperty("result", out var resultText))
                {
                    var resultStr = resultText.GetString();
                    if (!string.IsNullOrWhiteSpace(resultStr) && outputBuf.Length == 0)
                        outputBuf.Append(resultStr);
                }
            }
            catch (JsonException)
            {
                // Non-JSON line — log for diagnostics
                logger?.Warn($"Claude non-JSON output: {line}");
            }
        }

        // Drain stderr
        var stderr = await stderrTask;
        await process.WaitForExitAsync(ct);

        sink.WriteLine(); // Final newline

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            if (output == null && process.ExitCode != 0)
                AnsiConsole.MarkupLine($"[red]Claude stderr: {Markup.Escape(stderr.Trim())}[/]");
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

        return new ClaudeResult
        {
            Success = process.ExitCode == 0,
            Output = finalOutput,
            Stderr = stderr,
            ExitCode = process.ExitCode,
        };
    }

    public async Task<ClaudeResult> RunWithRetryAsync(
        string prompt,
        bool noTools = false,
        string? workingDirectory = null,
        RalphLogger? logger = null,
        TextWriter? output = null,
        CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            if (attempt > 1)
            {
                if (output == null)
                    AnsiConsole.MarkupLine(
                        $"[yellow]Retry attempt {attempt}/{maxRetries} (waiting {retryDelay}s)...[/]");
                logger?.Info($"Retry attempt {attempt}/{maxRetries}");
                await Task.Delay(retryDelay * 1000, ct);
            }

            logger?.Info($"Running Claude Code (attempt {attempt})");

            var result = await RunStreamAsync(prompt, noTools, workingDirectory, logger, output, ct);
            if (result.Success)
            {
                logger?.Info("Claude Code execution successful");
                return result;
            }

            logger?.Error($"Claude Code failed with exit code {result.ExitCode} (attempt {attempt})");
            if (output == null)
                AnsiConsole.MarkupLine($"[red]Claude Code failed (exit code: {result.ExitCode})[/]");
        }

        logger?.Error($"Claude Code failed after {maxRetries} attempts");
        if (output == null)
            AnsiConsole.MarkupLine($"[red]Claude Code failed after {maxRetries} attempts[/]");
        return new ClaudeResult { Success = false, ExitCode = 1 };
    }
}
