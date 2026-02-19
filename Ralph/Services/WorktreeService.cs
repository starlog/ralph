using Spectre.Console;

namespace Ralph.Services;

public class MergeResult
{
    public bool Success { get; set; }
    public List<string>? ConflictFiles { get; set; }
    public string? ErrorMessage { get; set; }
}

public class WorktreeService
{
    private readonly GitService _git;
    private readonly string _worktreeBase;

    public WorktreeService(GitService git, string worktreeBase = ".ralph-worktrees")
    {
        _git = git;
        _worktreeBase = worktreeBase;
    }

    public string WorktreeBase => _worktreeBase;

    /// <summary>
    /// 태스크를 위한 git worktree를 생성합니다.
    /// </summary>
    public async Task<string> CreateWorktreeAsync(
        string taskId, string baseBranch, RalphLogger? logger = null, CancellationToken ct = default)
    {
        var branchName = $"ralph/{taskId}";
        var worktreePath = Path.GetFullPath(Path.Combine(_worktreeBase, taskId));

        // 이미 존재하면 정리
        if (Directory.Exists(worktreePath))
        {
            logger?.Warn($"Worktree already exists for {taskId}, cleaning up...");
            await CleanupWorktreeAsync(taskId, logger, ct);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

        // stale worktree 참조 정리 후 브랜치 삭제
        await _git.RunAsync(["worktree", "prune"], ct: ct);
        await _git.RunAsync(["branch", "-D", branchName], ct: ct);

        // git worktree add -b ralph/{taskId} .ralph-worktrees/{taskId} {baseBranch}
        var (exitCode, output) = await _git.RunAsync(
            ["worktree", "add", "-b", branchName, worktreePath, baseBranch], ct: ct);

        if (exitCode != 0)
            throw new InvalidOperationException($"Failed to create worktree for {taskId}: {output}");

        logger?.Info($"Worktree created: {worktreePath} (branch: {branchName})");
        return worktreePath;
    }

    /// <summary>
    /// worktree의 브랜치를 대상 브랜치에 병합합니다.
    /// </summary>
    public async Task<MergeResult> MergeWorktreeAsync(
        string taskId, string targetBranch,
        string? mergeStrategy = null,
        RalphLogger? logger = null, CancellationToken ct = default)
    {
        var branchName = $"ralph/{taskId}";

        // 현재 브랜치가 target이 맞는지 확인
        var currentBranch = await _git.GetCurrentBranchAsync(ct: ct);
        if (currentBranch != targetBranch)
        {
            var (checkoutExit, checkoutOut) = await _git.RunAsync(["checkout", targetBranch], ct: ct);
            if (checkoutExit != 0)
                return new MergeResult { Success = false, ErrorMessage = $"Failed to checkout {targetBranch}: {checkoutOut}" };
        }

        // merge 실행
        var mergeArgs = new List<string> { "merge", branchName, "--no-ff", "-m", $"merge: {taskId} 태스크 병합" };
        if (mergeStrategy is "auto-theirs")
        {
            mergeArgs.InsertRange(2, ["-X", "theirs"]);
        }
        else if (mergeStrategy is "auto-ours")
        {
            mergeArgs.InsertRange(2, ["-X", "ours"]);
        }

        var (exitCode, output) = await _git.RunAsync(mergeArgs.ToArray(), ct: ct);

        if (exitCode == 0)
        {
            logger?.Info($"Merged {branchName} into {targetBranch}");
            return new MergeResult { Success = true };
        }

        // merge 충돌 감지
        var conflictFiles = await GetConflictFilesAsync(ct);
        logger?.Error($"Merge conflict for {branchName}: {output}");

        return new MergeResult
        {
            Success = false,
            ConflictFiles = conflictFiles,
            ErrorMessage = output
        };
    }

    /// <summary>
    /// merge를 중단합니다.
    /// </summary>
    public async Task AbortMergeAsync(CancellationToken ct = default)
    {
        await _git.RunAsync(["merge", "--abort"], ct: ct);
    }

    /// <summary>
    /// 충돌 파일 목록을 반환합니다.
    /// </summary>
    private async Task<List<string>> GetConflictFilesAsync(CancellationToken ct)
    {
        var (_, output) = await _git.RunAsync(["diff", "--name-only", "--diff-filter=U"], ct: ct);
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .ToList();
    }

    /// <summary>
    /// 특정 태스크의 worktree를 정리합니다.
    /// </summary>
    public async Task CleanupWorktreeAsync(
        string taskId, RalphLogger? logger = null, CancellationToken ct = default)
    {
        var worktreePath = Path.GetFullPath(Path.Combine(_worktreeBase, taskId));
        var branchName = $"ralph/{taskId}";

        // git worktree remove
        if (Directory.Exists(worktreePath))
        {
            var (exitCode, _) = await _git.RunAsync(["worktree", "remove", worktreePath, "--force"], ct: ct);
            if (exitCode != 0)
            {
                // 수동 삭제 시도
                try { Directory.Delete(worktreePath, true); }
                catch { /* best effort */ }
            }
        }

        // 브랜치 삭제
        await _git.RunAsync(["branch", "-D", branchName], ct: ct);
        logger?.Info($"Cleaned up worktree for {taskId}");
    }

    /// <summary>
    /// 모든 ralph worktree를 정리합니다.
    /// </summary>
    public async Task CleanupAllAsync(RalphLogger? logger = null, CancellationToken ct = default)
    {
        // git worktree prune
        await _git.RunAsync(["worktree", "prune"], ct: ct);

        // ralph worktree 브랜치 목록 가져오기
        var (_, branchOutput) = await _git.RunAsync(["branch", "--list", "ralph/*"], ct: ct);
        var branches = branchOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(b => b.Trim().TrimStart('*').Trim())
            .Where(b => b.StartsWith("ralph/"))
            .ToList();

        foreach (var branch in branches)
        {
            await _git.RunAsync(["branch", "-D", branch], ct: ct);
            logger?.Info($"Deleted branch: {branch}");
        }

        // worktree 디렉토리 정리
        if (Directory.Exists(_worktreeBase))
        {
            try { Directory.Delete(_worktreeBase, true); }
            catch { /* best effort */ }
        }

        logger?.Info("All ralph worktrees cleaned up");
    }

    /// <summary>
    /// 잔존하는 ralph worktree가 있는지 감지합니다.
    /// </summary>
    public async Task<List<string>> DetectStaleWorktreesAsync(CancellationToken ct = default)
    {
        var stale = new List<string>();

        var (_, output) = await _git.RunAsync(["worktree", "list", "--porcelain"], ct: ct);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith("branch ") && line.Contains("ralph/"))
            {
                var branch = line["branch refs/heads/".Length..].Trim();
                stale.Add(branch);
            }
        }

        return stale;
    }
}
