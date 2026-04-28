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
        IReadOnlyCollection<string>? declaredFiles = null,
        CancellationToken ct = default)
    {
        var commitMsg = commitTemplate
            .Replace("{taskId}", taskId)
            .Replace("{taskTitle}", title);

        if (!silent)
            AnsiConsole.MarkupLine("[blue]Committing changes...[/]");
        logger?.Info($"Committing: {commitMsg}");

        // Staging 전략:
        // - declaredFiles가 비어있지 않으면 그 경로만 명시적으로 staging.
        //   (병렬 worktree 격리 — task가 선언하지 않은 파일은 머지 표면에서 제외해
        //    의도하지 않은 cross-task 충돌을 방지.)
        // - declaredFiles가 null/빈 배열이면 fallback으로 -A 사용 (legacy 동작 유지).
        var declaredCount = declaredFiles?.Count ?? 0;
        if (declaredCount > 0)
        {
            await StageDeclaredFilesAsync(declaredFiles!, workingDirectory, logger, taskId, ct);
        }
        else
        {
            await RunAsync(["add", "-A"], workingDirectory, ct);
        }

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

        // Commit. 모델명을 attribution에 박지 않음 — sonnet/opus 어떤 모델이든 ralph 사용 시
        // 동일하게 표기되며, 모델 버전 outdated로 commit 메시지가 잘못되는 것을 방지.
        var fullMsg = $"{commitMsg}\n\nCo-Authored-By: Claude <noreply@anthropic.com>";
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
    /// declared 경로(상대/절대 모두 허용)를 worktree 기준으로 정규화하여 staging.
    /// 디스크에 없는 항목은 silently skip (task가 OutputFiles에 선언했지만 실제로 만들지 않은 경우 흔함).
    /// 민감 파일 패턴은 staging 자체를 건너뜀.
    /// </summary>
    private async Task StageDeclaredFilesAsync(
        IReadOnlyCollection<string> declaredFiles,
        string? workingDirectory,
        RalphLogger? logger,
        string taskId,
        CancellationToken ct)
    {
        var rootDir = workingDirectory is { Length: > 0 }
            ? Path.GetFullPath(workingDirectory)
            : Directory.GetCurrentDirectory();

        var staged = new List<string>();
        var skippedMissing = new List<string>();
        var skippedSensitive = new List<string>();

        foreach (var raw in declaredFiles)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var trimmed = raw.Trim().Replace('\\', '/');

            // 민감 파일 패턴은 declared 였더라도 staging 안 함
            if (IsSensitivePath(trimmed))
            {
                skippedSensitive.Add(trimmed);
                continue;
            }

            // 절대 경로면 worktree 루트 기준 상대 경로로 변환 (불가능하면 skip)
            string relative;
            if (Path.IsPathRooted(trimmed))
            {
                var fullDeclared = Path.GetFullPath(trimmed);
                if (!fullDeclared.StartsWith(rootDir, StringComparison.Ordinal))
                {
                    logger?.Warn($"[stage] {taskId}: {raw} is outside worktree — skipped");
                    continue;
                }
                relative = Path.GetRelativePath(rootDir, fullDeclared).Replace('\\', '/');
            }
            else
            {
                relative = trimmed.TrimStart('/');
            }

            // 디스크 존재 확인 (deleted 파일은 git add가 처리하므로 staging 시도해도 무방하지만,
            // 존재하지 않고 git에서도 모르는 경로는 git add가 fatal을 던져 commit 흐름이 깨짐.
            // 따라서 git ls-files로 tracked 여부를 확인하여 둘 중 하나면 staging 시도, 모두 아니면 skip).
            var fullPath = Path.Combine(rootDir, relative);
            var existsOnDisk = File.Exists(fullPath) || Directory.Exists(fullPath);

            bool tracked = false;
            if (!existsOnDisk)
            {
                var (lsExit, lsOut) = await RunAsync(
                    ["ls-files", "--error-unmatch", "--", relative], workingDirectory, ct);
                tracked = lsExit == 0 && !string.IsNullOrWhiteSpace(lsOut);
            }

            if (!existsOnDisk && !tracked)
            {
                skippedMissing.Add(relative);
                continue;
            }

            var (exit, output) = await RunAsync(["add", "--", relative], workingDirectory, ct);
            if (exit == 0)
            {
                staged.Add(relative);
            }
            else
            {
                logger?.Warn($"[stage] {taskId}: git add 실패 ({relative}): {output.Trim()}");
            }
        }

        if (staged.Count > 0)
            logger?.Info($"[stage] {taskId}: {staged.Count}건 staged — {string.Join(", ", staged.Take(5))}{(staged.Count > 5 ? "..." : "")}");
        if (skippedMissing.Count > 0)
            logger?.Info($"[stage] {taskId}: {skippedMissing.Count}건 declared지만 disk/index에 없음 — skipped");
        if (skippedSensitive.Count > 0)
            logger?.Warn($"[stage] {taskId}: {skippedSensitive.Count}건 민감 파일 패턴 — staging 거부: {string.Join(", ", skippedSensitive)}");
    }

    private static bool IsSensitivePath(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) name = path;
        foreach (var ext in SensitiveExtensions)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        }
        // 명시적 파일명 매칭 (e.g., credentials.json, id_rsa)
        if (name.Equals("credentials.json", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("id_rsa", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("id_ed25519", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith(".secret", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
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
    /// repo 루트 절대경로를 반환합니다 (worktree가 아닌 main worktree 기준).
    /// </summary>
    public async Task<string> GetRepoRootAsync(string? workingDirectory = null, CancellationToken ct = default)
    {
        var (exitCode, output) = await RunAsync(["rev-parse", "--show-toplevel"], workingDirectory, ct);
        return exitCode == 0 ? output.Trim() : Directory.GetCurrentDirectory();
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
