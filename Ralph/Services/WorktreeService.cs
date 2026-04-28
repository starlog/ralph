using System.Text.Json;
using Ralph.Models;
using Spectre.Console;

namespace Ralph.Services;

public class MergeResult
{
    public bool Success { get; set; }
    public List<string>? ConflictFiles { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// .ralph-logs/validation.jsonl에 기록되는 한 줄 형식. RalphJsonContext source-gen 대상.
/// </summary>
public sealed record ValidationLogEntry(
    string TaskId,
    string Timestamp,
    IReadOnlyList<string> Declared,
    IReadOnlyList<string> Actual,
    IReadOnlyList<string> Undeclared,
    IReadOnlyList<string> NotChanged);

/// <summary>
/// 머지 직전 declared(modifiedFiles ∪ outputFiles) vs actual(git diff base..HEAD) 비교 결과.
/// </summary>
public sealed record FileValidationResult(
    string TaskId,
    DateTimeOffset TimestampUtc,
    IReadOnlyList<string> Declared,
    IReadOnlyList<string> Actual,
    IReadOnlyList<string> Undeclared,
    IReadOnlyList<string> NotChanged,
    bool DiffFailed,
    string? DiffError)
{
    public bool HasUndeclared => Undeclared.Count > 0;
    public bool HasNotChanged => NotChanged.Count > 0;
}

public class WorktreeService
{
    // RalphJsonContext.Default를 chain해 trimming/AOT에서도 reflection fallback 없이 동작.
    private static readonly JsonSerializerOptions ValidationJsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = RalphJsonContext.Default,
    };

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
    /// 머지 직전 worktree에서 baseRef와 다른 tasks.json을 강제로 base 버전으로 되돌립니다.
    /// Claude가 worktree 안에서 tasks.json을 수정·커밋했을 때 발생하는 머지 충돌의
    /// 가장 흔한 케이스를 사전 차단합니다. 1차 방어(GuardTasksFileAsync)와 직교하며,
    /// 1차가 working-tree 변경을 막는 반면 본 메서드는 commit-tree 변경까지 본다.
    /// </summary>
    /// <returns>변경이 감지되었는지 여부 (true=정규화 시도, false=no-op 또는 사전 실패)</returns>
    public async Task<bool> NormalizeTasksJsonAsync(
        string taskId, string baseRef,
        string tasksFileName = "tasks.json",
        RalphLogger? logger = null,
        CancellationToken ct = default)
    {
        var worktreePath = Path.GetFullPath(Path.Combine(_worktreeBase, taskId));

        try
        {
            // worktree의 HEAD와 baseRef 사이에 tasksFileName 변경이 있는지 검사
            var (diffExit, diffOut) = await _git.RunAsync(
                ["diff", "--name-only", $"{baseRef}..HEAD", "--", tasksFileName],
                worktreePath, ct);

            if (diffExit != 0)
            {
                // diff 자체가 실패한 경우: 머지를 막지 않고 경고만 남긴다
                logger?.Warn(
                    $"[guard:pre-merge] NormalizeTasksJson({taskId}): git diff 실패. " +
                    $"머지는 계속 진행됩니다. detail: {diffOut.Trim()}");
                return false;
            }

            if (string.IsNullOrWhiteSpace(diffOut))
                return false; // baseRef와 동일 → no-op

            logger?.Warn(
                $"[guard:pre-merge] worktree '{taskId}'의 {tasksFileName}이 {baseRef}와 다릅니다. " +
                $"강제로 {baseRef} 버전으로 되돌립니다.");

            var (checkoutExit, checkoutOut) = await _git.RunAsync(
                ["checkout", baseRef, "--", tasksFileName],
                worktreePath, ct);

            if (checkoutExit != 0)
            {
                logger?.Warn(
                    $"[guard:pre-merge] NormalizeTasksJson({taskId}): " +
                    $"git checkout 실패. 머지는 계속 진행됩니다. detail: {checkoutOut.Trim()}");
                return true;
            }

            // checkout만으로는 worktree HEAD가 갱신되지 않아 머지 시 여전히 충돌이 발생한다.
            // 정규화 결과를 새 커밋으로 고정해 ralph/{taskId} tip의 tasksFile이 baseRef와 동일해지도록 한다.
            var (commitExit, commitOut) = await _git.RunAsync(
                ["commit", "-m", $"guard: {tasksFileName}을 {baseRef} 버전으로 정규화", "--", tasksFileName],
                worktreePath, ct);

            if (commitExit != 0)
            {
                logger?.Warn(
                    $"[guard:pre-merge] NormalizeTasksJson({taskId}): " +
                    $"정규화 커밋 실패. 머지에서 충돌이 발생할 수 있습니다. detail: {commitOut.Trim()}");
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C 시 graceful shutdown — 머지로 진행하지 말고 호출자에게 propagate.
            throw;
        }
        catch (Exception ex)
        {
            logger?.Warn(
                $"[guard:pre-merge] NormalizeTasksJson({taskId}): 예외 발생. " +
                $"머지는 계속 진행됩니다. detail: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 머지 직전 worktree의 브랜치를 현재 baseRef HEAD 위로 rebase합니다.
    ///
    /// 같은 batch에서 앞선 머지가 baseRef를 advance시켰는데 후속 worktree들은 여전히
    /// 옛 분기점에서 시작 — 3-way merge의 LCA가 옛 base가 되어 공유 파일(CLAUDE.md,
    /// 자동 생성 파일 등)에 불필요한 충돌이 발생합니다. 머지 직전에 rebase하면 LCA가
    /// 현재 base가 되어 깨끗한 fast-forward로 머지됩니다.
    ///
    /// rebase가 충돌하면 abort로 worktree를 깨끗하게 복원하고 false를 반환합니다 —
    /// 호출자는 기존 3-way merge 경로로 fallback해 동작 회귀를 막을 수 있습니다.
    /// </summary>
    public async Task<bool> AdvanceWorktreeOntoBaseAsync(
        string taskId, string baseRef, RalphLogger? logger = null, CancellationToken ct = default)
    {
        var worktreePath = Path.GetFullPath(Path.Combine(_worktreeBase, taskId));

        var (exitCode, output) = await _git.RunAsync(
            ["rebase", baseRef], worktreePath, ct);

        if (exitCode == 0)
        {
            logger?.Info($"[merge:advance] {taskId} rebased onto current {baseRef}");
            return true;
        }

        logger?.Warn(
            $"[merge:advance] {taskId} rebase 실패 — 3-way merge로 fallback. " +
            $"detail: {output.Trim()}");

        // rebase 중단으로 worktree를 깨끗한 상태로 복원 (다음 단계의 직접 머지가 가능하도록)
        var (abortExit, abortOut) = await _git.RunAsync(
            ["rebase", "--abort"], worktreePath, ct);
        if (abortExit != 0)
            logger?.Warn($"[merge:advance] {taskId} rebase --abort도 실패: {abortOut.Trim()}");

        return false;
    }

    /// <summary>
    /// 머지 직전 worktree HEAD와 baseRef 사이의 실제 변경 파일 집합을 declared 집합과
    /// 대조합니다. F2의 NormalizeTasksJsonAsync "이후"에 호출되어야 tasks.json
    /// 정규화 결과가 actual에서 빠지고, 진짜 undeclared만 남습니다.
    ///
    /// 결과는 .ralph-logs/validation.jsonl(또는 지정 경로)에 한 줄(append) JSON으로 누적되며,
    /// undeclared가 있으면 logger.Warn을 남깁니다. diff 자체가 실패하면 머지를 막지 않고
    /// DiffFailed=true로 반환합니다.
    /// </summary>
    public async Task<FileValidationResult> ValidateModifiedFilesAsync(
        string taskId,
        string baseRef,
        IReadOnlyCollection<string> declared,
        RalphLogger? logger = null,
        string validationLogPath = ".ralph-logs/validation.jsonl",
        CancellationToken ct = default)
    {
        var worktreePath = Path.GetFullPath(Path.Combine(_worktreeBase, taskId));
        var timestamp = DateTimeOffset.UtcNow;

        var (diffExit, diffOut) = await _git.RunAsync(
            ["diff", "--name-only", $"{baseRef}..HEAD"], worktreePath, ct);

        if (diffExit != 0)
        {
            var error = diffOut.Trim();
            logger?.Warn($"[validate:files] {taskId}: git diff 실패 — 검증 스킵. detail: {error}");
            return new FileValidationResult(
                taskId, timestamp,
                Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<string>(),
                DiffFailed: true, DiffError: error);
        }

        var actual = diffOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(NormalizeSlash)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var declaredNorm = declared
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeSlash)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var declaredSet = new HashSet<string>(declaredNorm, StringComparer.Ordinal);
        var actualSet = new HashSet<string>(actual, StringComparer.Ordinal);

        var undeclared = actual.Where(p => !declaredSet.Contains(p)).ToList();
        var notChanged = declaredNorm.Where(p => !actualSet.Contains(p)).ToList();

        if (undeclared.Count > 0)
        {
            var preview = string.Join(", ", undeclared.Take(3));
            var more = undeclared.Count > 3 ? $" (외 {undeclared.Count - 3}건)" : "";
            logger?.Warn(
                $"[validate:files] {taskId}: undeclared {undeclared.Count}건 — {preview}{more}");
        }

        var result = new FileValidationResult(
            taskId, timestamp,
            declaredNorm, actual, undeclared, notChanged,
            DiffFailed: false, DiffError: null);

        await AppendValidationLogAsync(result, validationLogPath, logger, ct);
        return result;
    }

    private static string NormalizeSlash(string path) =>
        string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');

    private static async Task AppendValidationLogAsync(
        FileValidationResult result, string validationLogPath,
        RalphLogger? logger, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(validationLogPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var record = new ValidationLogEntry(
                result.TaskId,
                result.TimestampUtc.ToString("o"),
                result.Declared,
                result.Actual,
                result.Undeclared,
                result.NotChanged);

            var line = JsonSerializer.Serialize(record, ValidationJsonOpts) + "\n";
            await File.AppendAllTextAsync(validationLogPath, line, ct);
        }
        catch (Exception ex)
        {
            // best-effort: validation 기록이 머지 흐름을 깨뜨리면 안 됨
            logger?.Warn($"[validate:files] {result.TaskId}: validation.jsonl 기록 실패 — {ex.Message}");
        }
    }

    // ValidationLogEntry는 namespace 레벨로 분리됨 (RalphJsonContext source-gen 등록용).

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
    /// 디렉터리/브랜치를 모두 정리하면 true, 한 단계라도 실패하면 false (호출자가
    /// 누적 카운트해서 최종 안내 메시지에 사용).
    /// </summary>
    public async Task<bool> CleanupWorktreeAsync(
        string taskId, RalphLogger? logger = null, CancellationToken ct = default)
    {
        var worktreePath = Path.GetFullPath(Path.Combine(_worktreeBase, taskId));
        var branchName = $"ralph/{taskId}";
        var ok = true;

        // git worktree remove
        if (Directory.Exists(worktreePath))
        {
            var (exitCode, output) = await _git.RunAsync(["worktree", "remove", worktreePath, "--force"], ct: ct);
            if (exitCode != 0)
            {
                logger?.Warn($"git worktree remove 실패 ({taskId}): {output.Trim()}");
                // 수동 삭제 시도
                try { Directory.Delete(worktreePath, true); }
                catch (Exception ex)
                {
                    logger?.Warn($"수동 디렉터리 삭제 실패 ({taskId}): {ex.Message}");
                    ok = false;
                }
            }
        }

        // 브랜치 삭제 (이미 머지된 후에도 -D는 성공)
        var (branchExit, branchOut) = await _git.RunAsync(["branch", "-D", branchName], ct: ct);
        if (branchExit != 0 && Directory.Exists(worktreePath))
        {
            // 디렉터리가 여전히 남아 있고 브랜치도 못 지우면 명백한 실패
            logger?.Warn($"git branch -D 실패 ({taskId}): {branchOut.Trim()}");
            ok = false;
        }

        if (ok) logger?.Info($"Cleaned up worktree for {taskId}");
        return ok;
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
            string? branch = null;
            if (line.StartsWith("branch refs/heads/"))
            {
                branch = line["branch refs/heads/".Length..].Trim();
            }
            else if (line.StartsWith("branch "))
            {
                // git이 "branch ralph/foo"처럼 refs/heads/ 없이 출력하는 경우 fallback.
                branch = line["branch ".Length..].Trim();
            }

            if (branch is { Length: > 0 } && branch.StartsWith("ralph/"))
                stale.Add(branch);
        }

        return stale;
    }
}
