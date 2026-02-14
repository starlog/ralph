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
    public async Task<ClaudeResult> RunStreamAsync(
        string prompt,
        bool noTools = false,
        RalphLogger? logger = null,
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

        // Build arguments via ArgumentList (safe escaping)
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--dangerously-skip-permissions");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--verbose");
        psi.ArgumentList.Add("--include-partial-messages");

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

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();

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

                if (type == "stream_event" && root.TryGetProperty("event", out var evt))
                {
                    var eventType = evt.TryGetProperty("type", out var et) ? et.GetString() : null;

                    if (eventType == "content_block_start")
                    {
                        Console.WriteLine();
                    }
                    else if (eventType == "content_block_delta"
                             && evt.TryGetProperty("delta", out var delta)
                             && delta.TryGetProperty("text", out var text))
                    {
                        var chunk = text.GetString() ?? "";
                        Console.Write(chunk);
                    }
                }
                else if (type == "assistant" && root.TryGetProperty("message", out var msg))
                {
                    if (msg.TryGetProperty("content", out var content))
                    {
                        foreach (var item in content.EnumerateArray())
                        {
                            if (item.TryGetProperty("text", out var txt))
                                output.AppendLine(txt.GetString());
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Non-JSON line — skip
            }
        }

        // Drain stderr
        var stderr = await stderrTask;
        await process.WaitForExitAsync(ct);

        Console.WriteLine(); // Final newline

        if (!string.IsNullOrWhiteSpace(stderr) && process.ExitCode != 0)
        {
            AnsiConsole.MarkupLine($"[red]Claude stderr: {Markup.Escape(stderr.Trim())}[/]");
            logger?.Error($"Claude stderr: {stderr.Trim()}");
        }

        return new ClaudeResult
        {
            Success = process.ExitCode == 0,
            Output = output.ToString(),
            Stderr = stderr,
            ExitCode = process.ExitCode,
        };
    }

    public async Task<ClaudeResult> RunWithRetryAsync(
        string prompt,
        bool noTools = false,
        RalphLogger? logger = null,
        CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            if (attempt > 1)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Retry attempt {attempt}/{maxRetries} (waiting {retryDelay}s)...[/]");
                logger?.Info($"Retry attempt {attempt}/{maxRetries}");
                await Task.Delay(retryDelay * 1000, ct);
            }

            logger?.Info($"Running Claude Code (attempt {attempt})");

            var result = await RunStreamAsync(prompt, noTools, logger, ct);
            if (result.Success)
            {
                logger?.Info("Claude Code execution successful");
                return result;
            }

            logger?.Error($"Claude Code failed with exit code {result.ExitCode} (attempt {attempt})");
            AnsiConsole.MarkupLine($"[red]Claude Code failed (exit code: {result.ExitCode})[/]");
        }

        logger?.Error($"Claude Code failed after {maxRetries} attempts");
        AnsiConsole.MarkupLine($"[red]Claude Code failed after {maxRetries} attempts[/]");
        return new ClaudeResult { Success = false, ExitCode = 1 };
    }
}
