using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

/// <summary>
/// WorktreeService.AdvanceWorktreeOntoBaseAsync의 rebase 충돌 처리를 검증.
/// batch 내 앞선 머지로 base가 advance된 상황에서 충돌/비충돌 시나리오를 임시 git repo로 테스트.
/// </summary>
[Collection("cost")]
public class RebaseConflictTests
{
    // 두 task가 같은 파일 같은 라인 수정 → 앞선 task 머지 후 rebase advance → RebaseConflict
    [Fact]
    public async Task SameLineConflict_ReturnsRebaseConflict_WorktreeClean()
    {
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("shared.txt", "line1\noriginal\nline3\n");
        await fix.CommitAllAsync("initial");

        // t1: shared.txt 2번째 라인 수정
        await fix.SetupWorktreeAsync("t1");
        await fix.WriteInWorktreeAsync("t1", "shared.txt", "line1\nt1-version\nline3\n");
        await fix.CommitInWorktreeAsync("t1", "[Task #t1] t1 change");

        // t2: 같은 파일 같은 라인을 다르게 수정
        await fix.SetupWorktreeAsync("t2");
        await fix.WriteInWorktreeAsync("t2", "shared.txt", "line1\nt2-version\nline3\n");
        await fix.CommitInWorktreeAsync("t2", "[Task #t2] t2 change");

        // t1이 먼저 main에 머지되어 base advance
        await MergeToMainAsync(fix, "t1");

        // t2 rebase advance → 같은 라인 충돌 발생해야 함
        var result = await fix.Worktree.AdvanceWorktreeOntoBaseAsync("t2", "main");

        Assert.False(result.Success);
        Assert.Equal(MergeFailureKind.RebaseConflict, result.FailureKind);

        // abort로 worktree가 깨끗한 상태로 복원됨 — t2의 원래 변경이 남아있어야 함.
        // Windows의 git autocrlf=true 설정으로 \r\n이 들어올 수 있어 정규화 후 비교.
        var actual = fix.ReadInWorktree("t2", "shared.txt").Replace("\r\n", "\n");
        Assert.Equal("line1\nt2-version\nline3\n", actual);
    }

    // 충돌 없는 독립 task는 같은 batch에서 정상 진행
    [Fact]
    public async Task IndependentTask_InSameBatch_SucceedsNormally()
    {
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("shared.txt", "original");
        await fix.WriteFileAsync("independent.txt", "original");
        await fix.CommitAllAsync("initial");

        // t1: shared.txt만 수정
        await fix.SetupWorktreeAsync("t1");
        await fix.WriteInWorktreeAsync("t1", "shared.txt", "t1-change");
        await fix.CommitInWorktreeAsync("t1", "[Task #t1] t1");

        // t3: independent.txt만 수정 (t1과 겹치지 않음)
        await fix.SetupWorktreeAsync("t3");
        await fix.WriteInWorktreeAsync("t3", "independent.txt", "t3-change");
        await fix.CommitInWorktreeAsync("t3", "[Task #t3] t3");

        // t1이 먼저 main에 머지
        await MergeToMainAsync(fix, "t1");

        // t3은 독립 파일만 수정했으므로 rebase advance 성공
        var result = await fix.Worktree.AdvanceWorktreeOntoBaseAsync("t3", "main");

        Assert.True(result.Success);
        Assert.Equal(MergeFailureKind.None, result.FailureKind);
        Assert.Equal("t3-change", fix.ReadInWorktree("t3", "independent.txt"));
        // main에서 t1이 머지한 변경도 worktree에 반영됨
        Assert.Equal("t1-change", fix.ReadInWorktree("t3", "shared.txt"));
    }

    // 정상 rebase (base가 advance되었지만 충돌 없음) → 통과, 변경 보존
    [Fact]
    public async Task NormalRebase_NoConflict_PassesAndPreservesChanges()
    {
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("a.txt", "original");
        await fix.CommitAllAsync("initial");

        // t1: worktree 전용 파일 추가
        await fix.SetupWorktreeAsync("t1");
        await fix.WriteInWorktreeAsync("t1", "wt-only.txt", "wt content");
        await fix.CommitInWorktreeAsync("t1", "[Task #t1] wt change");

        // main에서 전혀 다른 파일 추가 (t1과 충돌 없음)
        await fix.WriteFileAsync("main-only.txt", "main content");
        await fix.CommitAllAsync("main advance");

        var result = await fix.Worktree.AdvanceWorktreeOntoBaseAsync("t1", "main");

        Assert.True(result.Success);
        Assert.Equal(MergeFailureKind.None, result.FailureKind);
        // worktree 변경이 유지됨
        Assert.Equal("wt content", fix.ReadInWorktree("t1", "wt-only.txt"));
        // main의 advance도 worktree에 반영됨
        Assert.True(fix.FileExistsInWorktree("t1", "main-only.txt"));
    }

    // 충돌 파일 목록이 결과에 정확히 담겨 있는지
    [Fact]
    public async Task ConflictFilesInResult_AreAccurate()
    {
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("conflict.txt", "original");
        await fix.WriteFileAsync("no-conflict.txt", "unchanged");
        await fix.CommitAllAsync("initial");

        // t1: conflict.txt만 수정
        await fix.SetupWorktreeAsync("t1");
        await fix.WriteInWorktreeAsync("t1", "conflict.txt", "t1-change");
        await fix.CommitInWorktreeAsync("t1", "[Task #t1] t1");

        // t2: 같은 파일을 다르게 수정 + 충돌 없는 파일도 수정
        await fix.SetupWorktreeAsync("t2");
        await fix.WriteInWorktreeAsync("t2", "conflict.txt", "t2-change");
        await fix.WriteInWorktreeAsync("t2", "no-conflict.txt", "t2-no-conflict");
        await fix.CommitInWorktreeAsync("t2", "[Task #t2] t2");

        // t1이 먼저 main에 머지
        await MergeToMainAsync(fix, "t1");

        var result = await fix.Worktree.AdvanceWorktreeOntoBaseAsync("t2", "main");

        Assert.False(result.Success);
        Assert.Equal(MergeFailureKind.RebaseConflict, result.FailureKind);
        Assert.NotNull(result.ConflictFiles);
        Assert.Contains("conflict.txt", result.ConflictFiles);
        // no-conflict.txt는 충돌 파일 목록에 없어야 함
        Assert.DoesNotContain("no-conflict.txt", result.ConflictFiles);
    }

    // 회귀: 과거에는 worktree의 untracked 충돌 파일 때문에 rebase가 시작도 못 하고
    // blind --abort가 "fatal: No rebase in progress?"로 2차 실패해 FailureKind=Other가
    // 되어 batch 전체가 중단됐다. 이제는 PreRebaseCleanup이 rebase 직전에 untracked
    // 부산물을 청소해 이 케이스 자체가 발생하지 않는다 — main이 추가한 동일 경로가
    // 그대로 적용되어 rebase는 성공한다. (rebase 미시작 실패 시 abort 스킵 로직은
    // invalid baseRef / lock file 같은 비-dirty 사유에 대한 안전망으로 남아 있음.)
    [Fact]
    public async Task UntrackedCollidingFile_IsDiscardedByPreRebaseCleanup_AndRebaseSucceeds()
    {
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("seed.txt", "seed");
        await fix.CommitAllAsync("initial");

        await fix.SetupWorktreeAsync("t1");
        await fix.WriteInWorktreeAsync("t1", "wt-only.txt", "wt");
        await fix.CommitInWorktreeAsync("t1", "[Task #t1] add file");

        // main에서 동일 경로의 파일을 추가/커밋. 과거에는 이게 worktree의 untracked
        // 동명 파일과 충돌해 rebase 자체가 시작을 못 했다.
        await fix.WriteFileAsync("collide.txt", "main version");
        await fix.CommitAllAsync("main: add collide");

        var collidePath = Path.Combine(fix.WorktreeBase, "t1", "collide.txt");
        await File.WriteAllTextAsync(collidePath, "untracked local");

        var result = await fix.Worktree.AdvanceWorktreeOntoBaseAsync("t1", "main");

        // PreRebaseCleanup이 untracked collide.txt를 폐기 → rebase는 main의 commit을
        // 깨끗하게 적용해 성공.
        Assert.True(result.Success);
        Assert.Equal(MergeFailureKind.None, result.FailureKind);

        // 최종적으로 main이 가져온 버전이 worktree에 자리잡는다.
        Assert.Equal("main version", fix.ReadInWorktree("t1", "collide.txt"));
        // worktree의 declared commit도 보존
        Assert.Equal("wt", fix.ReadInWorktree("t1", "wt-only.txt"));
    }

    /// <summary>
    /// 헬퍼: ralph/{taskId} 브랜치를 main에 no-ff 머지. main 브랜치에서 직접 git 명령 실행.
    /// </summary>
    private static async Task MergeToMainAsync(GitFixture fix, string taskId)
    {
        var branchName = $"ralph/{taskId}";
        var (e1, o1) = await fix.Git.RunAsync(["checkout", "main"], fix.RepoDir);
        if (e1 != 0) throw new InvalidOperationException($"checkout main failed: {o1.Trim()}");

        var (e2, o2) = await fix.Git.RunAsync(
            ["merge", "--no-ff", "-m", $"merge: {taskId} 태스크 병합", branchName], fix.RepoDir);
        if (e2 != 0) throw new InvalidOperationException($"merge {branchName} failed: {o2.Trim()}");
    }
}
