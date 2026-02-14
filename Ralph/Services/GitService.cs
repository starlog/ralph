using System.Diagnostics;
using System.Text;
using Spectre.Console;

namespace Ralph.Services;

public class GitService
{
    private static readonly string[] SensitivePatterns =
    [
        ".env", ".env.*", "*.pem", "*.key", "*.p12", "*.pfx",
        "credentials.json", "service-account*.json",
        ".secret*", "*.secrets", "id_rsa", "id_ed25519"
    ];

    private static readonly string[] SensitiveExtensions =
        [".env", ".pem", ".key", ".p12", ".pfx", ".secrets"];

    public async Task<bool> IsRepoInitializedAsync(CancellationToken ct = default)
    {
        var (exitCode, _) = await RunAsync(["rev-parse", "--git-dir"], ct);
        return exitCode == 0;
    }

    public async Task InitAsync(RalphLogger? logger = null, CancellationToken ct = default)
    {
        AnsiConsole.MarkupLine("[yellow]Git 저장소가 없습니다. 초기화합니다...[/]");
        logger?.Info("Running git init");
        var (exitCode, output) = await RunAsync(["init"], ct);
        if (exitCode == 0)
        {
            AnsiConsole.MarkupLine("[green]Git 저장소 초기화 완료.[/]");
            logger?.Info($"git init: {output.Trim()}");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]git init 실패: {Markup.Escape(output.Trim())}[/]");
            logger?.Error($"git init failed: {output.Trim()}");
        }
    }

    public async Task<(int ExitCode, string Output)> RunAsync(
        string[] arguments, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, process.ExitCode == 0 ? stdout : stderr);
    }

    public async Task CommitChangesAsync(
        string taskId, string title, string commitTemplate,
        RalphLogger? logger = null, CancellationToken ct = default)
    {
        var commitMsg = commitTemplate
            .Replace("{taskId}", taskId)
            .Replace("{taskTitle}", title);

        AnsiConsole.MarkupLine("[blue]Committing changes...[/]");
        logger?.Info($"Committing: {commitMsg}");

        // Stage all files
        await RunAsync(["add", "-A"], ct);

        // Unstage sensitive file patterns
        foreach (var pattern in SensitivePatterns)
        {
            await RunAsync(["reset", "HEAD", "--", pattern], ct);
        }

        // Warn about sensitive untracked files
        var (_, statusOutput) = await RunAsync(["status", "--porcelain"], ct);
        var sensitiveLines = statusOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("??"))
            .Where(line =>
            {
                var file = line[3..].Trim();
                return SensitiveExtensions.Any(ext =>
                    file.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();

        if (sensitiveLines.Count > 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Warning: Sensitive files detected and excluded from commit:[/]");
            foreach (var line in sensitiveLines)
                AnsiConsole.WriteLine(line);
            logger?.Warn($"Sensitive files excluded: {string.Join(", ", sensitiveLines)}");
        }

        // Commit
        var fullMsg = $"{commitMsg}\n\nCo-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>";
        var (exitCode, _) = await RunAsync(["commit", "-m", fullMsg], ct);

        if (exitCode == 0)
        {
            AnsiConsole.MarkupLine($"[green]Committed: {Markup.Escape(commitMsg)}[/]");
            logger?.Info($"Commit successful: {commitMsg}");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]No changes to commit or commit failed.[/]");
            logger?.Warn("Commit failed or no changes");
        }
    }
}
