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
/// 머지 직전 declared(modifiedFiles ∪ outputFiles) vs actual(git diff base...HEAD) 비교 결과.
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

    public WorktreeService(GitService git, string worktreeBase = RalphPaths.WorktreeDir)
    {
        _git = git;
        _worktreeBase = worktreeBase;
    }

    public string WorktreeBase => _worktreeBase;

    /// <summary>
    /// 태스크를 위한 git worktree를 생성합니다.
    /// sharedObjects=true이면 `git worktree add --shared`로 .git objects를 공유해 디스크/IO를 절약합니다.
    /// 일부 환경(오래된 git 또는 비표준 빌드)은 `--shared`를 모를 수 있어, 첫 시도 실패 시 `--shared` 없이 한 번 더 시도합니다.
    /// </summary>
    public async Task<string> CreateWorktreeAsync(
        string taskId, string baseBranch, RalphLogger? logger = null,
        bool sharedObjects = false, CancellationToken ct = default)
    {
        logger ??= RalphLogger.Null;
        var branchName = RalphPaths.GetBranchName(taskId);
        var worktreePath = Path.GetFullPath(Path.Combine(_worktreeBase, taskId));

        // 이미 존재하면 정리
        if (Directory.Exists(worktreePath))
        {
            logger.Warn($"Worktree already exists for {taskId}, cleaning up...");
            await CleanupWorktreeAsync(taskId, logger, ct);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

        // stale worktree 참조 정리
        await _git.RunAsync(["worktree", "prune"], ct: ct);

        // 동명 브랜치가 이미 존재하면, ralph가 만든 것일 때만 삭제. 사용자가 직접 만든
        // ralph/* 브랜치를 silent하게 날리지 않도록 config 마커(또는 활성 worktree 연결)로 가드.
        if (await BranchExistsAsync(branchName, ct))
        {
            if (!await IsRalphManagedBranchAsync(branchName, ct))
            {
                throw new InvalidOperationException(
                    $"브랜치 '{branchName}'이 이미 존재하지만 ralph가 만든 것이 아닙니다. " +
                    $"silent 삭제를 거부합니다 — 해당 브랜치를 다른 이름으로 옮기거나 직접 정리한 뒤 다시 실행하세요.");
            }
            var (delExit, delOut) = await _git.RunAsync(["branch", "-D", branchName], ct: ct);
            if (delExit != 0)
                logger.Warn($"기존 ralph 브랜치 삭제 실패 ({taskId}): {delOut.Trim()}");
        }

        // git worktree add [--shared] -b ralph/{taskId} .ralph-worktrees/{taskId} {baseBranch}
        int exitCode;
        string output;
        if (sharedObjects)
        {
            (exitCode, output) = await _git.RunAsync(
                ["worktree", "add", "--shared", "-b", branchName, worktreePath, baseBranch], ct: ct);

            if (exitCode != 0)
            {
                logger.Warn($"--shared not supported, falling back ({taskId}): {output.Trim()}");
                (exitCode, output) = await _git.RunAsync(
                    ["worktree", "add", "-b", branchName, worktreePath, baseBranch], ct: ct);
            }
        }
        else
        {
            (exitCode, output) = await _git.RunAsync(
                ["worktree", "add", "-b", branchName, worktreePath, baseBranch], ct: ct);
        }

        if (exitCode != 0)
            throw new InvalidOperationException($"Failed to create worktree for {taskId}: {output}");

        // ralph가 만든 브랜치임을 표시 — 후속 cleanup이 사용자 브랜치를 건드리지 않도록.
        await MarkRalphManagedAsync(branchName, ct);

        logger.Info($"Worktree created: {worktreePath} (branch: {branchName}{(sharedObjects ? ", shared" : "")})");
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
        logger ??= RalphLogger.Null;
        var branchName = RalphPaths.GetBranchName(taskId);

        // 현재 브랜치가 target이 맞는지 확인
        var currentBranch = await _git.GetCurrentBranchAsync(ct: ct);
        if (currentBranch != targetBranch)
        {
            var (checkoutExit, checkoutOut) = await _git.RunAsync(["checkout", targetBranch], ct: ct);
            if (checkoutExit != 0)
                return new MergeResult { Success = false, ErrorMessage = $"Failed to checkout {targetBranch}: {checkoutOut}" };
        }

        // merge 실행
        var mergeArgs = new List<string> { "merge", "--no-ff", "-m", $"merge: {taskId} 태스크 병합" };
        if (mergeStrategy is "auto-theirs")
        {
            mergeArgs.InsertRange(1, ["-X", "theirs"]);
        }
        else if (mergeStrategy is "auto-ours")
        {
            mergeArgs.InsertRange(1, ["-X", "ours"]);
        }
        mergeArgs.Add(branchName);

        var (exitCode, output) = await _git.RunAsync(mergeArgs.ToArray(), ct: ct);

        if (exitCode == 0)
        {
            logger.Info($"Merged {branchName} into {targetBranch}");
            return new MergeResult { Success = true };
        }

        // base working tree에 untracked 파일이 있어 git이 데이터 손실 방지로 머지를 abort한 케이스.
        // 이 경우 머지가 시작도 못 했으므로 unmerged index가 비어 있어 ConflictFiles로는 잡히지 않고,
        // auto-theirs(-X)나 Claude resolver도 손쓸 게 없다. untracked blocker들을 백업으로 옮기고
        // 한 번 재시도해 plan 단계 부산물 같은 흔한 케이스를 자동 복구한다.
        var untrackedBlockers = ParseUntrackedOverwrites(output, logger);
        if (untrackedBlockers.Count > 0)
        {
            var repoRoot = await _git.GetRepoRootAsync(ct: ct);
            var backupDir = Path.Combine(
                repoRoot, RalphPaths.LogDir, RalphPaths.UntrackedBackupDirName,
                $"{taskId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
            var moved = new List<string>();
            foreach (var rel in untrackedBlockers)
            {
                try
                {
                    var src = Path.Combine(repoRoot, rel);
                    if (!File.Exists(src)) continue;
                    var dst = Path.Combine(backupDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Move(src, dst, overwrite: true);
                    moved.Add(rel);
                }
                catch (Exception ex)
                {
                    logger.Warn(
                        $"[merge:untracked-rescue] {taskId}: '{rel}' 백업 실패 — {ex.Message}");
                }
            }

            if (moved.Count > 0)
            {
                logger.Warn(
                    $"[merge:untracked-rescue] {taskId}: base 워크트리의 untracked {moved.Count}건을 " +
                    $"{backupDir}로 이동 후 머지 재시도 — {string.Join(", ", moved)}");
                AnsiConsole.MarkupLine(
                    $"  [yellow]ℹ[/] base 워크트리에 untracked 파일이 있어 머지가 막혔습니다. " +
                    $"{moved.Count}건을 백업 후 재머지: [dim]{Markup.Escape(backupDir)}[/]");

                var (retryExit, retryOut) = await _git.RunAsync(mergeArgs.ToArray(), ct: ct);
                if (retryExit == 0)
                {
                    logger.Info(
                        $"Merged {branchName} into {targetBranch} (after relocating {moved.Count} untracked file(s))");
                    return new MergeResult { Success = true };
                }
                // 재시도도 실패한 경우 — 일반 충돌 경로로 fall through
                output = retryOut;
            }
        }

        // merge 충돌 감지
        var conflictFiles = await GetConflictFilesAsync(ct);
        logger.Error($"Merge conflict for {branchName}: {output}");

        return new MergeResult
        {
            Success = false,
            ConflictFiles = conflictFiles,
            ErrorMessage = output
        };
    }

    /// <summary>
    /// `git merge`가 untracked working tree 파일과 충돌해 abort한 경우 출력에서 파일 목록을 추출한다.
    /// 메시지 패턴:
    ///   error: The following untracked working tree files would be overwritten by merge:
    ///   	subtract.py
    ///   	other.py
    ///   Please move or remove them before you merge.
    /// 다른 메시지(예: 일반 머지 충돌)일 때는 빈 리스트.
    /// </summary>
    internal static List<string> ParseUntrackedOverwrites(string mergeOutput, RalphLogger? logger = null)
    {
        logger ??= RalphLogger.Null;
        var result = new List<string>();
        if (string.IsNullOrEmpty(mergeOutput)) return result;
        if (!mergeOutput.Contains("untracked working tree files would be overwritten by merge"))
            return result;

        var lines = mergeOutput.Split('\n');
        var collecting = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Contains("untracked working tree files would be overwritten by merge"))
            {
                collecting = true;
                continue;
            }
            if (!collecting) continue;
            // 종료 마커
            if (line.StartsWith("Please move or remove", StringComparison.Ordinal)
                || line.StartsWith("Aborting", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(line))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                break;
            }
            // git은 파일명을 탭으로 들여쓰기 출력
            var path = line.TrimStart('\t', ' ');
            if (path.Length > 0) result.Add(path);
        }

        if (result.Count == 0)
        {
            var snippet = mergeOutput.Length > 200 ? mergeOutput[..200] + "..." : mergeOutput;
            logger.Warn($"[ParseUntrackedOverwrites] 'untracked overwrite' 패턴을 감지했으나 파일 목록 추출 실패. stderr 일부: {snippet}");
        }

        return result;
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
        logger ??= RalphLogger.Null;
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
                logger.Warn(
                    $"[guard:pre-merge] NormalizeTasksJson({taskId}): git diff 실패. " +
                    $"머지는 계속 진행됩니다. detail: {diffOut.Trim()}");
                return false;
            }

            if (string.IsNullOrWhiteSpace(diffOut))
                return false; // baseRef와 동일 → no-op

            logger.Warn(
                $"[guard:pre-merge] worktree '{taskId}'의 {tasksFileName}이 {baseRef}와 다릅니다. " +
                $"강제로 {baseRef} 버전으로 되돌립니다.");

            var (checkoutExit, checkoutOut) = await _git.RunAsync(
                ["checkout", baseRef, "--", tasksFileName],
                worktreePath, ct);

            if (checkoutExit != 0)
            {
                logger.Warn(
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
                logger.Warn(
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
            logger.Warn(
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
        logger ??= RalphLogger.Null;
        var worktreePath = Path.GetFullPath(Path.Combine(_worktreeBase, taskId));

        var (exitCode, output) = await _git.RunAsync(
            ["rebase", baseRef], worktreePath, ct);

        if (exitCode == 0)
        {
            logger.Info($"[merge:advance] {taskId} rebased onto current {baseRef}");
            return true;
        }

        logger.Warn(
            $"[merge:advance] {taskId} rebase 실패 — 3-way merge로 fallback. " +
            $"detail: {output.Trim()}");

        // rebase 중단으로 worktree를 깨끗한 상태로 복원 (다음 단계의 직접 머지가 가능하도록)
        var (abortExit, abortOut) = await _git.RunAsync(
            ["rebase", "--abort"], worktreePath, ct);
        if (abortExit != 0)
            logger.Warn($"[merge:advance] {taskId} rebase --abort도 실패: {abortOut.Trim()}");

        return false;
    }

    /// <summary>
    /// 머지 직전 worktree HEAD와 baseRef 사이의 실제 변경 파일 집합을 declared 집합과
    /// 대조합니다. F2의 NormalizeTasksJsonAsync "이후"에 호출되어야 tasks.json
    /// 정규화 결과가 actual에서 빠지고, 진짜 undeclared만 남습니다.
    ///
    /// `git diff baseRef...HEAD` (세-점)을 사용해 merge-base부터 HEAD까지의 변경만
    /// 비교합니다. 두-점(`..`)은 트리 단순 비교라 같은 batch에서 앞 태스크가 base에
    /// 먼저 머지된 경우 그 파일이 HEAD엔 없어 false-positive undeclared로 잡힙니다.
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
        string validationLogPath = RalphPaths.ValidationLedgerRelativePath,
        CancellationToken ct = default)
    {
        logger ??= RalphLogger.Null;
        var worktreePath = Path.GetFullPath(Path.Combine(_worktreeBase, taskId));
        var timestamp = DateTimeOffset.UtcNow;

        var (diffExit, diffOut) = await _git.RunAsync(
            ["diff", "--name-only", $"{baseRef}...HEAD"], worktreePath, ct);

        if (diffExit != 0)
        {
            var error = diffOut.Trim();
            logger.Warn($"[validate:files] {taskId}: git diff 실패 — 검증 스킵. detail: {error}");
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
            logger.Warn(
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
        RalphLogger logger, CancellationToken ct)
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
            logger.Warn($"[validate:files] {result.TaskId}: {RalphPaths.ValidationLedgerFileName} 기록 실패 — {ex.Message}");
        }
    }

    // ValidationLogEntry는 namespace 레벨로 분리됨 (RalphJsonContext source-gen 등록용).

    /// <summary>
    /// refs/heads/{branchName}이 존재하는지 확인합니다.
    /// </summary>
    private async Task<bool> BranchExistsAsync(string branchName, CancellationToken ct)
    {
        var (exit, _) = await _git.RunAsync(
            ["show-ref", "--verify", "--quiet", $"refs/heads/{branchName}"], ct: ct);
        return exit == 0;
    }

    /// <summary>
    /// 브랜치 생성 시점에 ralph 소유임을 표시. 이후 삭제 가드의 1차 신호.
    /// </summary>
    private async Task MarkRalphManagedAsync(string branchName, CancellationToken ct)
    {
        await _git.RunAsync(
            ["config", RalphPaths.GetManagedConfigKey(branchName), "true"], ct: ct);
    }

    /// <summary>
    /// ralph가 만든 브랜치인지 판정.
    ///   1) `branch.{name}.ralphManaged=true` config 마커가 있으면 managed.
    ///   2) 없더라도 `git worktree list`에서 해당 브랜치가 우리의 worktreeBase 산하 워크트리에
    ///      묶여 있으면 managed (마커 도입 이전 버전이 만든 브랜치를 위한 fallback).
    /// 두 신호 모두 없는 경우 사용자가 직접 만든 동명 브랜치로 간주하고 false 반환.
    /// </summary>
    private async Task<bool> IsRalphManagedBranchAsync(string branchName, CancellationToken ct)
    {
        var (cfgExit, cfgOut) = await _git.RunAsync(
            ["config", "--get", RalphPaths.GetManagedConfigKey(branchName)], ct: ct);
        if (cfgExit == 0 && cfgOut.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            return true;

        // legacy fallback: 활성 worktree와의 연결로 소유권 확인
        var (wtExit, wtOut) = await _git.RunAsync(["worktree", "list", "--porcelain"], ct: ct);
        if (wtExit != 0) return false;

        var worktreeBaseAbs = Path.GetFullPath(_worktreeBase);
        string? curWorktree = null;
        foreach (var raw in wtOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                curWorktree = line["worktree ".Length..].Trim();
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var b = line["branch ".Length..].Trim();
                if (b.StartsWith("refs/heads/", StringComparison.Ordinal))
                    b = b["refs/heads/".Length..];
                if (b == branchName && curWorktree != null
                    && IsUnderWorktreeBase(curWorktree, worktreeBaseAbs))
                {
                    // 발견 시 즉시 마커를 박아두면 다음 호출부터 빠른 경로로 통과.
                    await MarkRalphManagedAsync(branchName, ct);
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsUnderWorktreeBase(string worktreePath, string worktreeBaseAbs)
    {
        try
        {
            var full = Path.GetFullPath(worktreePath);
            var cmp = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return full.Equals(worktreeBaseAbs, cmp)
                || full.StartsWith(worktreeBaseAbs + Path.DirectorySeparatorChar, cmp)
                || full.StartsWith(worktreeBaseAbs + Path.AltDirectorySeparatorChar, cmp);
        }
        catch
        {
            return false;
        }
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
    /// 디렉터리/브랜치를 모두 정리하면 true, 한 단계라도 실패하면 false (호출자가
    /// 누적 카운트해서 최종 안내 메시지에 사용).
    /// </summary>
    public async Task<bool> CleanupWorktreeAsync(
        string taskId, RalphLogger? logger = null, CancellationToken ct = default)
    {
        logger ??= RalphLogger.Null;
        var worktreePath = Path.GetFullPath(Path.Combine(_worktreeBase, taskId));
        var branchName = RalphPaths.GetBranchName(taskId);
        var ok = true;

        // 워크트리 제거 전에 소유권을 판정한다 — 제거 후엔 worktree-list 기반 legacy fallback이
        // 끊어져 마커 없는 ralph 브랜치(이전 버전 산출물)를 사용자 브랜치로 오판할 수 있다.
        // BranchExists 체크는 추후 -D 단계에서 다시 수행한다 (사이에 다른 프로세스가 지웠을 수 있음).
        var branchExists = await BranchExistsAsync(branchName, ct);
        var branchManaged = branchExists && await IsRalphManagedBranchAsync(branchName, ct);

        // git worktree remove
        if (Directory.Exists(worktreePath))
        {
            var (exitCode, output) = await _git.RunAsync(["worktree", "remove", worktreePath, "--force"], ct: ct);
            if (exitCode != 0)
            {
                logger.Warn($"git worktree remove 실패 ({taskId}): {output.Trim()}");
                // 수동 삭제 시도
                try { Directory.Delete(worktreePath, true); }
                catch (Exception ex)
                {
                    logger.Warn($"수동 디렉터리 삭제 실패 ({taskId}): {ex.Message}");
                    ok = false;
                }
            }
        }

        // 브랜치 삭제 (이미 머지된 후에도 -D는 성공). ralph가 만든 브랜치만 삭제.
        if (branchExists && await BranchExistsAsync(branchName, ct))
        {
            if (branchManaged)
            {
                var (branchExit, branchOut) = await _git.RunAsync(["branch", "-D", branchName], ct: ct);
                if (branchExit != 0 && Directory.Exists(worktreePath))
                {
                    // 디렉터리가 여전히 남아 있고 브랜치도 못 지우면 명백한 실패
                    logger.Warn($"git branch -D 실패 ({taskId}): {branchOut.Trim()}");
                    ok = false;
                }
            }
            else
            {
                logger.Warn(
                    $"브랜치 '{branchName}'은 ralph가 만든 것이 아니어서 보존합니다. " +
                    $"수동으로 정리하려면 git branch -D {branchName}을 직접 실행하세요.");
            }
        }

        if (ok) logger.Info($"Cleaned up worktree for {taskId}");
        return ok;
    }

    /// <summary>
    /// 모든 ralph worktree를 정리합니다.
    /// </summary>
    public async Task CleanupAllAsync(RalphLogger? logger = null, CancellationToken ct = default)
    {
        logger ??= RalphLogger.Null;
        // git worktree prune
        await _git.RunAsync(["worktree", "prune"], ct: ct);

        // ralph worktree 브랜치 목록 가져오기
        var (_, branchOutput) = await _git.RunAsync(["branch", "--list", RalphPaths.BranchListGlob], ct: ct);
        var branches = branchOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(b => b.Trim().TrimStart('*').Trim())
            .Where(b => b.StartsWith(RalphPaths.BranchPrefix, StringComparison.Ordinal))
            .ToList();

        foreach (var branch in branches)
        {
            if (await IsRalphManagedBranchAsync(branch, ct))
            {
                await _git.RunAsync(["branch", "-D", branch], ct: ct);
                logger.Info($"Deleted branch: {branch}");
            }
            else
            {
                logger.Info($"Skipped non-ralph branch: {branch} (ralph가 만든 것이 아님)");
            }
        }

        // worktree 디렉토리 정리
        if (Directory.Exists(_worktreeBase))
        {
            try { Directory.Delete(_worktreeBase, true); }
            catch (Exception ex)
            {
                logger.Warn(
                    $"worktree 베이스 디렉터리 삭제 실패 ({_worktreeBase}): {ex.Message} — " +
                    $"'ralph --worktree-cleanup'으로 재시도하세요");
            }
        }

        logger.Info("All ralph worktrees cleaned up");
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

            if (branch is { Length: > 0 } && branch.StartsWith(RalphPaths.BranchPrefix, StringComparison.Ordinal))
                stale.Add(branch);
        }

        return stale;
    }

    /// <summary>
    /// mid-task 상태(uncommitted 변경 또는 base 위로 진행된 커밋)인 worktree만 추려서 보고.
    /// 반환되는 각 항목은 사용자에게 "이 worktree에 작업이 진행 중일 수 있습니다"를 알리는 용도.
    /// </summary>
    public async Task<List<MidTaskWorktreeInfo>> DetectMidTaskWorktreesAsync(
        string baseBranch, CancellationToken ct = default)
    {
        var result = new List<MidTaskWorktreeInfo>();
        if (!Directory.Exists(_worktreeBase)) return result;

        foreach (var dir in Directory.GetDirectories(_worktreeBase))
        {
            ct.ThrowIfCancellationRequested();
            var taskId = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(taskId)) continue;

            var (statusExit, statusOut) = await _git.RunAsync(
                ["status", "--porcelain"], dir, ct);
            var hasUncommitted = statusExit == 0 && !string.IsNullOrWhiteSpace(statusOut);

            // base..HEAD에 커밋이 있으면 mid-task 진행분
            var (countExit, countOut) = await _git.RunAsync(
                ["rev-list", "--count", $"{baseBranch}..HEAD"], dir, ct);
            int aheadCount = 0;
            if (countExit == 0 && int.TryParse(countOut.Trim(), out var n)) aheadCount = n;

            if (hasUncommitted || aheadCount > 0)
            {
                result.Add(new MidTaskWorktreeInfo(
                    TaskId: taskId,
                    WorktreePath: Path.GetFullPath(dir),
                    AheadCount: aheadCount,
                    HasUncommitted: hasUncommitted));
            }
        }

        return result;
    }
}

/// <summary>
/// 중단/재개 시나리오에서 사용자에게 보여주기 위한 mid-task 워크트리 정보.
/// </summary>
public sealed record MidTaskWorktreeInfo(
    string TaskId,
    string WorktreePath,
    int AheadCount,
    bool HasUncommitted);
