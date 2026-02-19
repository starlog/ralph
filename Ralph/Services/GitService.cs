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
        var (exitCode, _) = await RunAsync(["rev-parse", "--git-dir"], ct: ct);
        return exitCode == 0;
    }

    public async Task InitAsync(RalphLogger? logger = null, CancellationToken ct = default)
    {
        AnsiConsole.MarkupLine("[yellow]Git 저장소가 없습니다. 초기화합니다...[/]");
        logger?.Info("Running git init");
        var (exitCode, output) = await RunAsync(["init"], ct: ct);
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
        string[] arguments, string? workingDirectory = null, CancellationToken ct = default)
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

        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

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
        RalphLogger? logger = null, string? workingDirectory = null,
        bool silent = false,
        CancellationToken ct = default)
    {
        var commitMsg = commitTemplate
            .Replace("{taskId}", taskId)
            .Replace("{taskTitle}", title);

        if (!silent)
            AnsiConsole.MarkupLine("[blue]Committing changes...[/]");
        logger?.Info($"Committing: {commitMsg}");

        // Stage all files
        await RunAsync(["add", "-A"], workingDirectory, ct);

        // Unstage sensitive file patterns
        foreach (var pattern in SensitivePatterns)
        {
            await RunAsync(["reset", "HEAD", "--", pattern], workingDirectory, ct);
        }

        // Warn about sensitive untracked files
        var (_, statusOutput) = await RunAsync(["status", "--porcelain"], workingDirectory, ct);
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
            if (!silent)
                AnsiConsole.MarkupLine(
                    "[yellow]Warning: Sensitive files detected and excluded from commit:[/]");
            foreach (var line in sensitiveLines)
            {
                if (!silent)
                    AnsiConsole.WriteLine(line);
            }
            logger?.Warn($"Sensitive files excluded: {string.Join(", ", sensitiveLines)}");
        }

        // Commit
        var fullMsg = $"{commitMsg}\n\nCo-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>";
        var (exitCode, _) = await RunAsync(["commit", "-m", fullMsg], workingDirectory, ct);

        if (exitCode == 0)
        {
            if (!silent)
                AnsiConsole.MarkupLine($"[green]Committed: {Markup.Escape(commitMsg)}[/]");
            logger?.Info($"Commit successful: {commitMsg}");
        }
        else
        {
            if (!silent)
                AnsiConsole.MarkupLine("[yellow]No changes to commit or commit failed.[/]");
            logger?.Warn("Commit failed or no changes");
        }
    }

    /// <summary>
    /// 현재 브랜치 이름을 반환합니다.
    /// </summary>
    public async Task<string> GetCurrentBranchAsync(string? workingDirectory = null, CancellationToken ct = default)
    {
        var (exitCode, output) = await RunAsync(["rev-parse", "--abbrev-ref", "HEAD"], workingDirectory, ct);
        return exitCode == 0 ? output.Trim() : "main";
    }

    /// <summary>
    /// 커밋이 하나라도 존재하는지 확인합니다.
    /// </summary>
    public async Task<bool> HasCommitsAsync(CancellationToken ct = default)
    {
        var (exitCode, _) = await RunAsync(["rev-parse", "HEAD"], ct: ct);
        return exitCode == 0;
    }

    /// <summary>
    /// 빈 초기 커밋을 생성합니다 (worktree 사용을 위해 필요).
    /// </summary>
    public async Task EnsureInitialCommitAsync(RalphLogger? logger = null, CancellationToken ct = default)
    {
        if (await HasCommitsAsync(ct))
            return;

        logger?.Info("No commits found, creating initial commit for worktree support");
        AnsiConsole.MarkupLine("[yellow]커밋이 없습니다. worktree 지원을 위해 초기 커밋을 생성합니다...[/]");

        var (exitCode, output) = await RunAsync(
            ["commit", "--allow-empty", "-m", "chore: 초기 커밋 (ralph 워크트리 지원)"], ct: ct);

        if (exitCode == 0)
        {
            AnsiConsole.MarkupLine("[green]초기 커밋 생성 완료.[/]");
            logger?.Info("Initial empty commit created");
        }
        else
        {
            logger?.Error($"Failed to create initial commit: {output}");
            throw new InvalidOperationException($"초기 커밋 생성 실패: {output}");
        }
    }
}
