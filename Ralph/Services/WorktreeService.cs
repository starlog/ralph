using System.Text;
using System.Text.Json;
using Ralph.Commands;
using Ralph.Models;
using Spectre.Console;

namespace Ralph.Services;

public enum MergeFailureKind
{
    None,
    MergeConflict,
    RebaseConflict,
    UntrackedOverwrite,
    Other,
}

public class MergeResult
{
    public bool Success { get; set; }
    public MergeFailureKind FailureKind { get; set; } = MergeFailureKind.None;
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

    // fix2 #4: 워크트리 루트에 떨어지는 ralph 마커 파일 (D 신호) 및 commit trailer 키 (C 신호).
    // 마커 파일은 git에 추적되지 않는다 (ralph가 add하지 않음).
    private const string MarkerFileName = ".ralph-marker";
    private const string MarkerSchemaVersion = "v1";
    private const string TrailerKey = "Ralph-Task-Id";

    private readonly GitService _git;
    private readonly string _worktreeBase;

    /// <summary>
    /// fix2 #4: 브랜치 삭제 안전성 판정 결과. (A∨B) AND (C∨D∨Unknown) 모델.
    ///   SafeToDelete    — A∨B 통과 + ralph 시그니처(C 또는 D) 확인.
    ///   NotRalphManaged — A∨B 모두 실패 (사용자 브랜치 — 기존 보존 흐름).
    ///   HoldUserOwned   — A∨B 통과했지만 reflog/커밋이 사용자 소유로 식별 → 차단.
    ///   HoldUnverified  — A∨B 통과했지만 ralph 시그니처를 확인할 수 없음 → 보수적 보류.
    /// </summary>
    private enum BranchSafeDeleteVerdict
    {
        SafeToDelete,
        NotRalphManaged,
        HoldUserOwned,
        HoldUnverified,
    }

    private enum SignatureKind { Ralph, UserOwned, Unknown }
    private enum MarkerState { RalphValid, Mismatch, Missing }

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

        // ralph artifact 경로가 .git/info/exclude에 등록되어 있는지 보장하고,
        // 이미 인덱스에 tracked되어 있으면 fail-fast (rebase preflight 일괄 실패 예방).
        var repoRootForGuard = await _git.GetRepoRootAsync(ct: ct);
        await RalphIgnoreGuard.EnsureAsync(_git, repoRootForGuard, logger, ct);

        // stale worktree 참조 정리
        await _git.RunAsync(["worktree", "prune"], ct: ct);

        // 동명 브랜치가 이미 존재하면, ralph가 만든 것일 때만 삭제. 사용자가 직접 만든
        // ralph/* 브랜치를 silent하게 날리지 않도록 config 마커(또는 활성 worktree 연결)로 가드.
        // fix2 #4: 1차 신호(A∨B)에 더해 reflog/커밋 시그니처(C) 또는 .ralph-marker(D)도 확인한다.
        if (await BranchExistsAsync(branchName, ct))
        {
            var verdict = await VerifySafeToDeleteAsync(branchName, taskId, worktreePath, ct);
            switch (verdict)
            {
                case BranchSafeDeleteVerdict.SafeToDelete:
                    var (delExit, delOut) = await _git.RunAsync(["branch", "-D", branchName], ct: ct);
                    if (delExit != 0)
                        logger.Warn($"기존 ralph 브랜치 삭제 실패 ({taskId}): {delOut.Trim()}");
                    break;
                case BranchSafeDeleteVerdict.NotRalphManaged:
                    throw new InvalidOperationException(
                        $"브랜치 '{branchName}'이 이미 존재하지만 ralph가 만든 것이 아닙니다. " +
                        $"silent 삭제를 거부합니다 — 해당 브랜치를 다른 이름으로 옮기거나 직접 정리한 뒤 다시 실행하세요.");
                case BranchSafeDeleteVerdict.HoldUserOwned:
                case BranchSafeDeleteVerdict.HoldUnverified:
                    throw new InvalidOperationException(
                        $"브랜치 '{branchName}'은 ralph 표시는 있으나 안전 검증 실패({verdict})로 silent 삭제를 거부합니다. " +
                        $"'git log {branchName} -1'로 내용을 확인 후 'git branch -D {branchName}'으로 직접 정리한 뒤 다시 실행하세요.");
            }
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

        // fix2 #4: 워크트리 루트에 .ralph-marker 파일을 떨어뜨려 D 신호를 확보. 마커는 git에
        // 추적되지 않으며, 워크트리가 살아 있는 동안만 의미가 있다 (cleanup의 디렉터리 제거 단계
        // 이후엔 자연 소멸). 쓰기 실패는 worktree 생성 흐름을 깨지 않는다 — C 신호로 폴백 가능.
        await TryWriteMarkerFileAsync(taskId, branchName, worktreePath, logger, ct);

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
                return new MergeResult
                {
                    Success = false,
                    FailureKind = MergeFailureKind.Other,
                    ErrorMessage = $"Failed to checkout {targetBranch}: {checkoutOut}",
                };
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

        // untracked overwrite로 시작했지만 재시도까지 실패한 경우엔 unmerged 파일이 없을 수 있다 —
        // 그럴 땐 UntrackedOverwrite로 라벨링, 그 외엔 일반 MergeConflict.
        var failureKind = (untrackedBlockers.Count > 0 && conflictFiles.Count == 0)
            ? MergeFailureKind.UntrackedOverwrite
            : MergeFailureKind.MergeConflict;

        return new MergeResult
        {
            Success = false,
            FailureKind = failureKind,
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
    /// rebase가 충돌하면 abort로 worktree를 깨끗하게 복원하고
    /// FailureKind=RebaseConflict + ConflictFiles로 분류해 반환합니다.
    /// rebase가 시작도 못 하고 실패한 경우(dirty tree, invalid baseRef 등)는 abort할
    /// 상태가 없으므로 abort를 호출하지 않고 곧장 RebaseConflict로 분류합니다 —
    /// 이전에는 무조건 abort를 호출해 "fatal: No rebase in progress?"로 2차 실패하고
    /// FailureKind=Other가 되어 batch 전체를 중단시켰습니다.
    /// abort를 시도했는데 실제로 실패한 경우만 FailureKind=Other (worktree dirty).
    /// 호출자는 분류에 따라 task만 실패 처리하거나 batch를 중단합니다 (silent 3-way fallback 없음).
    /// </summary>
    public async Task<MergeResult> AdvanceWorktreeOntoBaseAsync(
        string taskId, string baseRef, RalphLogger? logger = null,
        bool strictCleanup = false, CancellationToken ct = default)
    {
        logger ??= RalphLogger.Null;
        var worktreePath = Path.GetFullPath(Path.Combine(_worktreeBase, taskId));

        // 옵션 B: rebase 직전 worktree를 깨끗하게 만든다. declared 파일은 이미 HEAD에
        // commit되어 있고, 잔존 변경(undeclared tracked 수정 또는 untracked 부산물)은
        // declared-only 정책상 어차피 머지 표면 밖이므로 폐기해도 새 손실이 없다.
        // testing task가 흔히 흘리는 coverage / vitest cache / package-lock 같은
        // 부산물이 "cannot rebase: You have unstaged changes" preflight 실패로 batch를
        // 깨뜨리던 케이스를 일관되게 차단.
        // strictCleanup=true면 source 파일이 폐기 대상이면 fail-fast로 throw.
        await PreRebaseCleanupAsync(taskId, worktreePath, logger, strictCleanup, ct);

        var (exitCode, output) = await _git.RunAsync(
            ["rebase", baseRef], worktreePath, ct);

        if (exitCode == 0)
        {
            logger.Info($"[merge:advance] {taskId} rebased onto current {baseRef}");
            return new MergeResult { Success = true };
        }

        // 충돌 파일을 abort 전에 캡처 — abort 후 unmerged index가 비워진다.
        var conflictFiles = await GetRebaseConflictFilesAsync(worktreePath, ct);

        // rebase가 실제로 진행 중인지 확인. dirty tree/invalid baseRef 등으로 시작도
        // 못 한 경우 abort할 상태가 없어 호출 자체가 "No rebase in progress?"로 실패.
        var rebaseInProgress = await IsRebaseInProgressAsync(worktreePath, ct);

        // 옵션 A: rebase가 시작도 못 한 경우 어떤 파일이 막고 있는지 보이도록 status를
        // 캡처해 detail에 포함. PreRebaseCleanup으로 dirty-tree 케이스는 거의 사라졌지만,
        // invalid baseRef / lock file 등 비-dirty 사유에 여전히 유용.
        var detail = output.Trim();
        if (!rebaseInProgress)
        {
            var snapshot = await TryCaptureStatusSnapshotAsync(worktreePath, ct);
            if (snapshot.Length > 0)
                detail = detail + "\nworktree status:\n" + snapshot;
        }

        logger.Warn(
            $"[merge:advance] {taskId} rebase 실패 — RebaseConflict로 분류, " +
            $"task만 실패 처리 (in-progress={rebaseInProgress}). detail: {detail}");

        if (rebaseInProgress)
        {
            var (abortExit, abortOut) = await _git.RunAsync(
                ["rebase", "--abort"], worktreePath, ct);
            if (abortExit != 0)
            {
                logger.Error(
                    $"[merge:advance] {taskId} rebase --abort 실패: {abortOut.Trim()}. " +
                    $"worktree가 더러운 상태일 수 있습니다.");
                return new MergeResult
                {
                    Success = false,
                    FailureKind = MergeFailureKind.Other,
                    ConflictFiles = conflictFiles,
                    ErrorMessage = $"rebase abort failed: {abortOut.Trim()}",
                };
            }
        }
        else
        {
            logger.Info(
                $"[merge:advance] {taskId} rebase가 시작 전 단계에서 실패 — abort 스킵.");
        }

        return new MergeResult
        {
            Success = false,
            FailureKind = MergeFailureKind.RebaseConflict,
            ConflictFiles = conflictFiles,
            ErrorMessage = detail,
        };
    }

    /// <summary>
    /// rebase 직전 worktree의 미선언 잔존 변경을 폐기한다.
    /// declared 파일은 <see cref="WorktreeTaskRunner"/>가 이미 HEAD로 commit했으므로
    /// <c>git reset --hard HEAD</c>는 그것들을 건드리지 않고, undeclared tracked 수정만
    /// 되돌린다. 이어지는 <c>git clean -fd</c>는 untracked 부산물을 제거하되
    /// .git/info/exclude(<see cref="RalphIgnoreGuard"/>)에 등재된 ralph artifact 디렉터리는
    /// 자동으로 보존하며, 워크트리 루트의 .ralph-marker(stale 감지용 D 신호)는
    /// 명시적 -e 패턴으로 보존한다.
    /// 마지막으로 <c>git submodule update --init --recursive</c>를 호출해 부모 인덱스에
    /// 기록된 submodule SHA로 working tree를 정렬한다 — test 도중 submodule HEAD가 어긋나
    /// 부모가 <c>M submodule</c>로 보고하던 케이스를 차단. submodule이 없는 repo에서는
    /// near-no-op이며 실패해도 흐름을 깨지 않는다.
    /// </summary>
    private async Task PreRebaseCleanupAsync(
        string taskId, string worktreePath, RalphLogger logger,
        bool strictCleanup, CancellationToken ct)
    {
        var snapshot = await TryCaptureStatusSnapshotAsync(worktreePath, ct);
        if (snapshot.Length > 0)
        {
            var lineCount = snapshot.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            logger.Info(
                $"[merge:advance] {taskId} pre-rebase 정리 — 미선언 변경 {lineCount}건 폐기:\n  " +
                snapshot.Replace("\n", "\n  "));

            // 분류: source 파일이 undeclared로 남아있으면 plan에서 outputFiles 누락된
            // 진짜 코드일 가능성이 높다. 시각적으로 알리거나 (strict면) 즉시 중단.
            var report = CleanupClassifier.Classify(snapshot);
            if (report.SourceFiles.Count > 0)
            {
                var preview = string.Join("\n  ", report.SourceFiles.Take(10));
                var more = report.SourceFiles.Count - Math.Min(10, report.SourceFiles.Count);
                var msg =
                    $"[red]⚠ {taskId}: pre-rebase cleanup이 미선언 소스 파일 " +
                    $"{report.SourceFiles.Count}개를 폐기하려 합니다.[/]\n" +
                    $"[yellow]plan에서 outputFiles/modifiedFiles 누락 가능성 — " +
                    $"머지 후 base에서 import 깨짐을 유발할 수 있습니다.[/]\n" +
                    $"폐기 대상:\n  {Spectre.Console.Markup.Escape(preview)}" +
                    (more > 0 ? $"\n  ... (+{more}개)" : "");
                Spectre.Console.AnsiConsole.MarkupLine(msg);
                logger.Warn(
                    $"[merge:advance] {taskId} undeclared source files about to be discarded: " +
                    string.Join(", ", report.SourceFiles));

                if (strictCleanup)
                {
                    var bare =
                        $"{taskId}: pre-rebase cleanup이 미선언 소스 파일 " +
                        $"{report.SourceFiles.Count}개를 폐기하려 합니다 (--strict-cleanup 활성). " +
                        $"plan의 outputFiles에 추가하거나 task prompt에서 해당 파일을 만들지 않게 수정하세요.\n" +
                        $"파일: {string.Join(", ", report.SourceFiles)}";
                    throw new RalphUserException(bare);
                }
            }
        }

        var (resetExit, resetOut) = await _git.RunAsync(
            ["reset", "--hard", "HEAD"], worktreePath, ct);
        if (resetExit != 0)
        {
            logger.Warn(
                $"[merge:advance] {taskId} pre-rebase reset 실패 — 계속 진행: {resetOut.Trim()}");
        }

        var (cleanExit, cleanOut) = await _git.RunAsync(
            ["clean", "-fd", "-e", ".ralph-marker"], worktreePath, ct);
        if (cleanExit != 0)
        {
            logger.Warn(
                $"[merge:advance] {taskId} pre-rebase clean 실패 — 계속 진행: {cleanOut.Trim()}");
        }

        // submodule이 있는 repo에서만 의미 있는 단계. .gitmodules가 없으면 git이 빠르게
        // 0-exit으로 빠진다. 있는 경우에만 부모 index에 기록된 SHA로 submodule working
        // tree를 강제 정렬해 "M submodule" 상태가 rebase preflight를 막지 않게 한다.
        var gitmodulesPath = Path.Combine(worktreePath, ".gitmodules");
        if (File.Exists(gitmodulesPath))
        {
            var (subExit, subOut) = await _git.RunAsync(
                ["submodule", "update", "--init", "--recursive"], worktreePath, ct);
            if (subExit != 0)
            {
                logger.Warn(
                    $"[merge:advance] {taskId} pre-rebase submodule update 실패 — 계속 진행: {subOut.Trim()}");
            }
            else
            {
                logger.Info($"[merge:advance] {taskId} pre-rebase submodule 정렬 완료");
            }
        }
    }

    /// <summary>
    /// <c>git status --porcelain</c>을 안전하게 캡처. 실패하거나 비어 있으면 빈 문자열.
    /// </summary>
    private async Task<string> TryCaptureStatusSnapshotAsync(
        string worktreePath, CancellationToken ct)
    {
        var (statusExit, statusOut) = await _git.RunAsync(
            ["status", "--porcelain"], worktreePath, ct);
        if (statusExit != 0) return "";
        var trimmed = statusOut.Trim();
        return trimmed;
    }

    /// <summary>
    /// post-merge smoke test 전용 격리 worktree(`<repoRoot>/.ralph-smoke`)를 보장합니다.
    /// 존재하지 않으면 detached로 새로 만들고, 존재하면 baseBranch HEAD로 reset해 재사용합니다.
    /// 빌드 산출물(bin/, obj/, node_modules/, *.tsbuildinfo)이 master worktree에 떨어지지 않도록
    /// 격리하는 것이 목적이며, 캐시 유지를 위해 batch 간 재사용합니다.
    /// 반환값은 smoke worktree의 절대 경로. 실패 시 null을 반환합니다 (호출자가 fallback 결정).
    /// </summary>
    public async Task<string?> EnsureSmokeWorktreeAsync(
        string repoRoot, string baseBranch, RalphLogger? logger = null,
        CancellationToken ct = default)
    {
        logger ??= RalphLogger.Null;
        var smokePath = Path.GetFullPath(Path.Combine(repoRoot, RalphPaths.SmokeWorktreeDir));

        // 모든 git 호출은 repoRoot에서 실행해 호출자 CWD에 의존하지 않게 한다.
        // (smoke worktree 자체에 대한 작업만 smokePath에서 실행)

        // .git/info/exclude 보장 + tracked 감지 (idempotent — task worktree 진입점과
        // 둘 다에서 호출되어도 안전).
        await RalphIgnoreGuard.EnsureAsync(_git, repoRoot, logger, ct);

        // 동일 경로의 stale worktree 참조 정리 — 디렉터리는 사라졌는데 git이 기억하는 경우.
        await _git.RunAsync(["worktree", "prune"], repoRoot, ct);

        if (!Directory.Exists(smokePath))
        {
            var (addExit, addOut) = await _git.RunAsync(
                ["worktree", "add", "--detach", smokePath, baseBranch], repoRoot, ct);
            if (addExit != 0)
            {
                logger.Warn(
                    $"[smoke-worktree] 생성 실패 — fallback to repoRoot. detail: {addOut.Trim()}");
                return null;
            }
            logger.Info($"[smoke-worktree] created: {smokePath} @ {baseBranch}");
            return smokePath;
        }

        // 재사용 — baseBranch HEAD로 detached reset. 이전 batch의 빌드 산출물(.gitignore되지
        // 않은 것 포함)이 인덱스에 남아있을 수 있으므로 reset --hard로 깔끔히 정리.
        var (resetExit, resetOut) = await _git.RunAsync(
            ["reset", "--hard", baseBranch], smokePath, ct);
        if (resetExit != 0)
        {
            logger.Warn(
                $"[smoke-worktree] reset 실패 — 재생성 시도. detail: {resetOut.Trim()}");
            // 손상되었을 가능성 — 제거 후 새로 만든다.
            await _git.RunAsync(
                ["worktree", "remove", "--force", smokePath], repoRoot, ct);
            try { if (Directory.Exists(smokePath)) Directory.Delete(smokePath, recursive: true); }
            catch { /* best-effort */ }
            var (addExit2, addOut2) = await _git.RunAsync(
                ["worktree", "add", "--detach", smokePath, baseBranch], repoRoot, ct);
            if (addExit2 != 0)
            {
                logger.Warn(
                    $"[smoke-worktree] 재생성도 실패 — fallback to repoRoot. detail: {addOut2.Trim()}");
                return null;
            }
        }

        logger.Info($"[smoke-worktree] reused: {smokePath} @ {baseBranch}");
        return smokePath;
    }

    /// <summary>
    /// 워크트리에 rebase가 진행 중인지 확인합니다. <c>git rev-parse --git-path</c>로
    /// 워크트리별 git dir의 rebase-merge / rebase-apply 디렉터리 경로를 얻고
    /// 실제 존재 여부로 판정합니다.
    /// </summary>
    private async Task<bool> IsRebaseInProgressAsync(string worktreePath, CancellationToken ct)
    {
        foreach (var name in new[] { "rebase-merge", "rebase-apply" })
        {
            var (exit, output) = await _git.RunAsync(
                ["rev-parse", "--git-path", name], worktreePath, ct);
            if (exit != 0) continue;
            var path = output.Trim();
            if (path.Length == 0) continue;
            if (!Path.IsPathRooted(path))
                path = Path.Combine(worktreePath, path);
            if (Directory.Exists(path)) return true;
        }
        return false;
    }

    /// <summary>
    /// rebase 도중 unmerged 상태인 파일 목록을 캡처합니다 (abort 호출 전에 사용).
    /// diff가 실패해도 빈 리스트로 안전하게 반환 — 진단 정보 누락만 발생.
    /// </summary>
    private async Task<List<string>> GetRebaseConflictFilesAsync(
        string worktreePath, CancellationToken ct)
    {
        var (_, output) = await _git.RunAsync(
            ["diff", "--name-only", "--diff-filter=U"], worktreePath, ct);
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .Where(f => f.Length > 0)
            .ToList();
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

    /// <summary>
    /// fix2 #4: 브랜치 삭제 안전성 종합 판정. (A∨B) AND (C∨D∨Unknown) 모델.
    /// 워크트리 디렉터리가 아직 살아 있는 시점에 호출해야 D 신호(.ralph-marker)가 의미가 있다.
    /// </summary>
    private async Task<BranchSafeDeleteVerdict> VerifySafeToDeleteAsync(
        string branchName, string taskId, string worktreePath, CancellationToken ct)
    {
        // (A∨B) — config 마커 또는 활성 worktree 바인딩
        if (!await IsRalphManagedBranchAsync(branchName, ct))
            return BranchSafeDeleteVerdict.NotRalphManaged;

        // (D) 마커 파일이 살아 있고 유효 → 즉시 통과
        var marker = await ProbeMarkerFileAsync(taskId, branchName, worktreePath, ct);
        if (marker == MarkerState.RalphValid)
            return BranchSafeDeleteVerdict.SafeToDelete;

        // (C) reflog/커밋 시그니처
        var sig = await ProbeRalphSignatureAsync(branchName, ct);

        // 마커가 명백히 다른 task로 어긋나면 보수적 보류 (외부 도구 개입 가능성)
        if (marker == MarkerState.Mismatch)
            return BranchSafeDeleteVerdict.HoldUnverified;

        return sig switch
        {
            SignatureKind.Ralph => BranchSafeDeleteVerdict.SafeToDelete,
            SignatureKind.UserOwned => BranchSafeDeleteVerdict.HoldUserOwned,
            _ => BranchSafeDeleteVerdict.HoldUnverified,
        };
    }

    /// <summary>
    /// fix2 #4 — D 신호: 워크트리 루트의 <c>.ralph-marker</c>를 읽고 task-id/branch/schema가
    /// 일치하는지 확인. 파일 부재 또는 읽기 실패는 Missing(=확인 불가, 흐름 차단 안 함).
    /// </summary>
    private static Task<MarkerState> ProbeMarkerFileAsync(
        string taskId, string branchName, string worktreePath, CancellationToken ct)
    {
        try
        {
            var path = Path.Combine(Path.GetFullPath(worktreePath), MarkerFileName);
            if (!File.Exists(path)) return Task.FromResult(MarkerState.Missing);

            var content = File.ReadAllText(path, Encoding.UTF8);
            var kv = ParseMarkerKv(content);

            if (!kv.TryGetValue("schema", out var schema) || schema != MarkerSchemaVersion)
                return Task.FromResult(MarkerState.Mismatch);

            kv.TryGetValue("task-id", out var fileTaskId);
            kv.TryGetValue("branch", out var fileBranch);

            if (string.Equals(fileTaskId, taskId, StringComparison.Ordinal)
                && string.Equals(fileBranch, branchName, StringComparison.Ordinal))
                return Task.FromResult(MarkerState.RalphValid);

            return Task.FromResult(MarkerState.Mismatch);
        }
        catch
        {
            // best-effort: 파일 IO 실패는 흐름을 막지 않음 (Missing으로 폴백 → C 신호 차례)
            return Task.FromResult(MarkerState.Missing);
        }
    }

    private static Dictionary<string, string> ParseMarkerKv(string content)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var idx = line.IndexOf(':');
            if (idx <= 0 || idx == line.Length - 1) continue;
            var key = line[..idx].Trim();
            var val = line[(idx + 1)..].Trim();
            if (key.Length > 0) dict[key] = val;
        }
        return dict;
    }

    /// <summary>
    /// fix2 #4 — C 신호: 분기점 이후(base..tip) 커밋들에서 ralph 시그니처를 추정.
    ///   Ralph     — base..tip의 커밋이 0개(워크트리 생성 직후 사용자 손대지 않음) 또는,
    ///               trailer(<c>Ralph-Task-Id:</c>) 또는 ralph가 자동 생성하는 제목 패턴
    ///               (<c>[Task #...]</c>, <c>guard:</c>, <c>merge:</c>)이 하나라도 보임.
    ///   UserOwned — base..tip에 커밋이 있지만 ralph 시그니처를 가진 것이 하나도 없음.
    ///   Unknown   — reflog가 만료/없거나 git 명령이 실패해 분기점을 알 수 없음 → 식별 불가.
    ///
    /// 분기점 sha는 브랜치 reflog의 가장 오래된 entry로 추정한다 — `git worktree add -b` 또는
    /// `git branch` 직후의 reflog 첫 entry는 분기점 sha를 가리키므로 base ref를 모르더라도
    /// base..tip 구간을 식별할 수 있다. base 자체의 사용자 커밋(`initial` 등)이 시그니처
    /// 판정에 끼어드는 false-positive를 방지한다.
    /// </summary>
    private async Task<SignatureKind> ProbeRalphSignatureAsync(string branchName, CancellationToken ct)
    {
        // 1) 분기점 sha를 reflog의 가장 오래된 entry로부터 추정.
        var (refExit, refOut) = await _git.RunAsync(
            ["reflog", "--format=%H", $"refs/heads/{branchName}"], ct: ct);

        string? baseSha = null;
        if (refExit == 0)
        {
            var refLines = refOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // reflog 첫 줄이 최신, 마지막 줄이 가장 오래된 entry → 분기점 후보.
            if (refLines.Length > 0)
                baseSha = refLines[^1].TrimEnd('\r').Trim();
        }

        // 2) base..tip 범위의 커밋만 본다.
        var trailerExpr = $"%(trailers:key={TrailerKey},valueonly=true,separator=%x20)";
        var range = !string.IsNullOrEmpty(baseSha)
            ? $"{baseSha}..refs/heads/{branchName}"
            : $"refs/heads/{branchName}";

        var (exit, output) = await _git.RunAsync(
            ["log", $"--format=%H%x09%s%x09{trailerExpr}", "-n", "20", range],
            ct: ct);

        if (exit != 0) return SignatureKind.Unknown;
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // base..tip이 비어 있음 → 워크트리 생성 직후, 사용자 손댄 흔적 없음. baseSha를 식별
        // 했을 때만 Ralph로 단정 (식별 못 한 경우엔 reflog 만료 가능성 → 보수적 Unknown).
        if (lines.Length == 0)
            return baseSha != null ? SignatureKind.Ralph : SignatureKind.Unknown;

        var hasRalph = false;
        var hasNonRalph = false;
        foreach (var raw in lines)
        {
            var parts = raw.TrimEnd('\r').Split('\t');
            if (parts.Length < 2) continue;
            var subject = parts[1];
            var trailer = parts.Length >= 3 ? parts[2].Trim() : "";

            var isRalph =
                trailer.Length > 0
                || subject.StartsWith("[Task #", StringComparison.Ordinal)
                || subject.StartsWith("guard:", StringComparison.Ordinal)
                || subject.StartsWith("merge:", StringComparison.Ordinal);

            if (isRalph) hasRalph = true;
            else hasNonRalph = true;
        }

        if (hasRalph) return SignatureKind.Ralph;
        if (hasNonRalph) return SignatureKind.UserOwned;
        return SignatureKind.Unknown;
    }

    /// <summary>
    /// fix2 #4 — D 신호 작성: 워크트리 루트의 <c>.ralph-marker</c>에 KV 포맷으로 기록.
    /// 쓰기 실패는 흐름을 깨지 않음 (C 신호로 폴백 가능).
    /// </summary>
    private static async Task TryWriteMarkerFileAsync(
        string taskId, string branchName, string worktreePath, RalphLogger logger, CancellationToken ct)
    {
        try
        {
            var fullWorktree = Path.GetFullPath(worktreePath);
            var path = Path.Combine(fullWorktree, MarkerFileName);
            var sb = new StringBuilder();
            sb.Append("schema: ").Append(MarkerSchemaVersion).Append('\n');
            sb.Append("ralph-version: ").Append(DisplayHelpers.Version).Append('\n');
            sb.Append("task-id: ").Append(taskId).Append('\n');
            sb.Append("branch: ").Append(branchName).Append('\n');
            sb.Append("created-at: ").Append(DateTimeOffset.UtcNow.ToString("o")).Append('\n');
            sb.Append("worktree-path: ").Append(fullWorktree).Append('\n');
            await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.Warn($"[guard] {taskId}: .ralph-marker 작성 실패 — {ex.Message}. C 신호(reflog/trailer)로 폴백합니다.");
        }
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

        // 워크트리 제거 전에 소유권/안전성을 판정한다 — 제거 후엔
        //   (1) worktree-list 기반 legacy fallback(B 신호)이 끊어져 마커 없는 ralph 브랜치를
        //       사용자 브랜치로 오판할 수 있고,
        //   (2) 워크트리 루트의 .ralph-marker(D 신호)도 디렉터리와 함께 사라진다.
        // BranchExists 체크는 추후 -D 단계에서 다시 수행한다 (사이에 다른 프로세스가 지웠을 수 있음).
        var branchExists = await BranchExistsAsync(branchName, ct);
        var verdict = branchExists
            ? await VerifySafeToDeleteAsync(branchName, taskId, worktreePath, ct)
            : BranchSafeDeleteVerdict.NotRalphManaged;

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

        // 브랜치 삭제 (이미 머지된 후에도 -D는 성공). fix2 #4: 가드 강화 모델 적용 —
        //   SafeToDelete   → -D 진행 (정상 ralph 워크트리 라이프사이클)
        //   NotRalphManaged → 기존 보존 메시지 (사용자 브랜치)
        //   HoldUserOwned  → ralph 표시는 있지만 커밋/reflog가 사용자 소유로 식별 → 차단 + 안내
        //   HoldUnverified → 표시는 있지만 시그니처 확인 불가(reflog 만료, 마커 부재) → 보수적 보류
        if (branchExists && await BranchExistsAsync(branchName, ct))
        {
            switch (verdict)
            {
                case BranchSafeDeleteVerdict.SafeToDelete:
                    var (branchExit, branchOut) = await _git.RunAsync(["branch", "-D", branchName], ct: ct);
                    if (branchExit != 0 && Directory.Exists(worktreePath))
                    {
                        // 디렉터리가 여전히 남아 있고 브랜치도 못 지우면 명백한 실패
                        logger.Warn($"git branch -D 실패 ({taskId}): {branchOut.Trim()}");
                        ok = false;
                    }
                    break;

                case BranchSafeDeleteVerdict.NotRalphManaged:
                    logger.Warn(
                        $"브랜치 '{branchName}'은 ralph가 만든 것이 아니어서 보존합니다. " +
                        $"수동으로 정리하려면 git branch -D {branchName}을 직접 실행하세요.");
                    break;

                case BranchSafeDeleteVerdict.HoldUserOwned:
                    logger.Warn(
                        $"[ralph] 브랜치 {branchName}은(는) ralph 표시는 있으나 안전 검증 실패. 수동 삭제 필요. " +
                        $"reflog/커밋이 사용자 소유로 식별되어 삭제를 보류합니다. " +
                        $"직접 만든 브랜치라면 'git config --unset {RalphPaths.GetManagedConfigKey(branchName)}' 후 그대로 두세요. " +
                        $"ralph 잔여물이라면 'git branch -D {branchName}'으로 수동 정리하세요.");
                    break;

                case BranchSafeDeleteVerdict.HoldUnverified:
                    logger.Warn(
                        $"[ralph] 브랜치 {branchName}은(는) ralph 표시는 있으나 안전 검증 실패. 수동 삭제 필요. " +
                        $"(reflog 만료 또는 .ralph-marker 부재 — ralph 시그니처 확인 불가). " +
                        $"'git log {branchName} -1'로 확인 후 ralph가 만든 것이 맞다면 'git branch -D {branchName}'으로 정리하세요.");
                    break;
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

        // smoke test 격리 worktree도 함께 정리 — 사용자가 --worktree-cleanup을 호출했을 때
        // 빌드 캐시까지 깔끔하게 비우는 것이 일관된 기대치.
        await TryRemoveSmokeWorktreeAsync(logger, ct);

        logger.Info("All ralph worktrees cleaned up");
    }

    /// <summary>
    /// <c>.ralph-smoke</c> 격리 worktree를 best-effort로 제거합니다.
    /// repoRoot 자동 추론 — `git rev-parse --show-toplevel` 실패 시 작업 자체를 스킵.
    /// </summary>
    private async Task TryRemoveSmokeWorktreeAsync(RalphLogger logger, CancellationToken ct)
    {
        try
        {
            var repoRoot = await _git.GetRepoRootAsync(ct: ct);
            var smokePath = Path.GetFullPath(Path.Combine(repoRoot, RalphPaths.SmokeWorktreeDir));
            if (!Directory.Exists(smokePath)) return;

            await _git.RunAsync(["worktree", "remove", "--force", smokePath], ct: ct);
            if (Directory.Exists(smokePath))
                Directory.Delete(smokePath, recursive: true);
            logger.Info($"[smoke-worktree] removed: {smokePath}");
        }
        catch (Exception ex)
        {
            logger.Warn($"[smoke-worktree] 제거 실패: {ex.Message}");
        }
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
