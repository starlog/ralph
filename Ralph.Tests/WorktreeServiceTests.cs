using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

/// <summary>
/// WorktreeService의 머지 직전 단계(NormalizeTasksJson, ValidateModifiedFiles,
/// AdvanceWorktreeOntoBase)와 브랜치 삭제 가드(fix2 #4)를 실제 git fixture로 검증.
/// CleanupWorktreeAsync는 CWD 의존 git 호출을 사용하므로 "cost" collection으로 직렬화.
/// </summary>
[Collection("cost")]
public class WorktreeServiceTests
{
    // ─── NormalizeTasksJsonAsync ─────────────────────────────────────────────

    [Fact]
    public async Task NormalizeTasksJson_no_change_returns_false()
    {
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("tasks.json", "{\"tasks\":[]}");
        await fix.CommitAllAsync("initial");
        await fix.SetupWorktreeAsync("t1");

        var changed = await fix.Worktree.NormalizeTasksJsonAsync("t1", "main");

        Assert.False(changed);
    }

    [Fact]
    public async Task NormalizeTasksJson_reverts_committed_modification()
    {
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("tasks.json", "ORIGINAL");
        await fix.CommitAllAsync("initial");
        await fix.SetupWorktreeAsync("t1");

        await fix.WriteInWorktreeAsync("t1", "tasks.json", "MODIFIED-IN-WT");
        await fix.CommitInWorktreeAsync("t1", "wt change");

        var changed = await fix.Worktree.NormalizeTasksJsonAsync("t1", "main");

        Assert.True(changed);
        Assert.Equal("ORIGINAL", fix.ReadInWorktree("t1", "tasks.json"));
    }

    [Fact]
    public async Task NormalizeTasksJson_only_touches_tasks_json_not_other_files()
    {
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("tasks.json", "ORIG");
        await fix.WriteFileAsync("other.txt", "OTHER-ORIG");
        await fix.CommitAllAsync("initial");
        await fix.SetupWorktreeAsync("t1");

        await fix.WriteInWorktreeAsync("t1", "tasks.json", "WT");
        await fix.WriteInWorktreeAsync("t1", "other.txt", "WT-OTHER");
        await fix.CommitInWorktreeAsync("t1", "wt change");

        await fix.Worktree.NormalizeTasksJsonAsync("t1", "main");

        Assert.Equal("ORIG", fix.ReadInWorktree("t1", "tasks.json"));
        Assert.Equal("WT-OTHER", fix.ReadInWorktree("t1", "other.txt"));
    }

    // ─── ValidateModifiedFilesAsync ──────────────────────────────────────────

    [Fact]
    public async Task ValidateModifiedFiles_clean_match_no_findings()
    {
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("a.txt", "A");
        await fix.WriteFileAsync("b.txt", "B");
        await fix.CommitAllAsync("initial");
        await fix.SetupWorktreeAsync("t1");

        await fix.WriteInWorktreeAsync("t1", "a.txt", "A-mod");
        await fix.WriteInWorktreeAsync("t1", "b.txt", "B-mod");
        await fix.CommitInWorktreeAsync("t1", "modify both");

        var declared = new HashSet<string> { "a.txt", "b.txt" };
        var result = await fix.Worktree.ValidateModifiedFilesAsync(
            "t1", "main", declared, validationLogPath: fix.ValidationLogPath);

        Assert.False(result.DiffFailed);
        Assert.False(result.HasUndeclared);
        Assert.False(result.HasNotChanged);
        Assert.Equal(2, result.Actual.Count);
    }

    [Fact]
    public async Task ValidateModifiedFiles_undeclared_file_flagged()
    {
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("declared.txt", "D");
        await fix.WriteFileAsync("undeclared.txt", "U");
        await fix.CommitAllAsync("initial");
        await fix.SetupWorktreeAsync("t1");

        await fix.WriteInWorktreeAsync("t1", "declared.txt", "D-mod");
        await fix.WriteInWorktreeAsync("t1", "undeclared.txt", "U-mod");
        await fix.CommitInWorktreeAsync("t1", "modify both");

        var declared = new HashSet<string> { "declared.txt" };
        var result = await fix.Worktree.ValidateModifiedFilesAsync(
            "t1", "main", declared, validationLogPath: fix.ValidationLogPath);

        Assert.False(result.DiffFailed);
        Assert.True(result.HasUndeclared);
        Assert.Single(result.Undeclared);
        Assert.Contains("undeclared.txt", result.Undeclared);
        Assert.False(result.HasNotChanged);
    }

    [Fact]
    public async Task ValidateModifiedFiles_notChanged_files_flagged()
    {
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("a.txt", "A");
        await fix.CommitAllAsync("initial");
        await fix.SetupWorktreeAsync("t1");

        // worktree에서 아무것도 수정 안 함

        var declared = new HashSet<string> { "a.txt", "b.txt" };
        var result = await fix.Worktree.ValidateModifiedFilesAsync(
            "t1", "main", declared, validationLogPath: fix.ValidationLogPath);

        Assert.False(result.DiffFailed);
        Assert.False(result.HasUndeclared);
        Assert.True(result.HasNotChanged);
        Assert.Equal(2, result.NotChanged.Count);
    }

    [Fact]
    public async Task ValidateModifiedFiles_path_normalization_handles_backslash()
    {
        // declared가 backslash 사용 시에도 git의 forward slash 출력과 매칭되어야 함
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("src/a.txt", "A");
        await fix.CommitAllAsync("initial");
        await fix.SetupWorktreeAsync("t1");

        await fix.WriteInWorktreeAsync("t1", "src/a.txt", "modified");
        await fix.CommitInWorktreeAsync("t1", "modify");

        var declared = new HashSet<string> { @"src\a.txt" };
        var result = await fix.Worktree.ValidateModifiedFilesAsync(
            "t1", "main", declared, validationLogPath: fix.ValidationLogPath);

        Assert.False(result.HasUndeclared);
        Assert.False(result.HasNotChanged);
    }

    [Fact]
    public async Task ValidateModifiedFiles_ignores_files_main_advanced_with_after_branch_diverged()
    {
        // 회귀 케이스: 같은 batch에서 앞 태스크가 main에 먼저 머지된 뒤, 다음 태스크의
        // 워크트리는 그 파일을 갖고 있지 않다. 두-점 diff(`base..HEAD`)는 트리 비교라
        // main의 신규 파일이 HEAD에 없으면 false-positive undeclared로 잡혔다.
        // 세-점 diff(`base...HEAD`)는 merge-base 기준이므로 이 false-positive를 막는다.
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("seed.txt", "S");
        await fix.CommitAllAsync("initial");
        await fix.SetupWorktreeAsync("t2");

        // t2는 자기 파일만 만든다 (subtract-impl 역할).
        await fix.WriteInWorktreeAsync("t2", "subtract.py", "def subtract(a,b): return a-b");
        await fix.CommitInWorktreeAsync("t2", "subtract impl");

        // 그 사이 main에는 다른 태스크(add-impl)가 먼저 머지되어 add.py가 추가됨.
        await fix.WriteFileAsync("add.py", "def add(a,b): return a+b");
        await fix.CommitAllAsync("add-impl merged");

        var declared = new HashSet<string> { "subtract.py" };
        var result = await fix.Worktree.ValidateModifiedFilesAsync(
            "t2", "main", declared, validationLogPath: fix.ValidationLogPath);

        Assert.False(result.DiffFailed);
        Assert.False(result.HasUndeclared);
        Assert.DoesNotContain("add.py", result.Actual);
        Assert.Contains("subtract.py", result.Actual);
    }

    [Fact]
    public async Task ValidateModifiedFiles_appends_to_jsonl_log()
    {
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("a.txt", "A");
        await fix.CommitAllAsync("initial");
        await fix.SetupWorktreeAsync("t1");

        await fix.WriteInWorktreeAsync("t1", "a.txt", "A-mod");
        await fix.CommitInWorktreeAsync("t1", "modify");

        var declared = new HashSet<string> { "a.txt" };
        await fix.Worktree.ValidateModifiedFilesAsync(
            "t1", "main", declared, validationLogPath: fix.ValidationLogPath);

        Assert.True(File.Exists(fix.ValidationLogPath));
        var line = (await File.ReadAllLinesAsync(fix.ValidationLogPath)).Single();
        Assert.Contains("\"taskId\":\"t1\"", line);
        Assert.Contains("\"actual\":[\"a.txt\"]", line);
    }

    // ─── AdvanceWorktreeOntoBaseAsync (#8 머지 직전 rebase) ──────────────────

    [Fact]
    public async Task AdvanceWorktreeOntoBase_succeeds_on_disjoint_changes()
    {
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("a.txt", "A");
        await fix.CommitAllAsync("initial");
        await fix.SetupWorktreeAsync("t1");

        await fix.WriteInWorktreeAsync("t1", "wt-only.txt", "wt content");
        await fix.CommitInWorktreeAsync("t1", "wt change");

        // main만 수정 (worktree와 서로 다른 파일)
        await fix.WriteFileAsync("main-only.txt", "main content");
        await fix.CommitAllAsync("main advance");

        var result = await fix.Worktree.AdvanceWorktreeOntoBaseAsync("t1", "main");

        Assert.True(result.Success);
        Assert.Equal(MergeFailureKind.None, result.FailureKind);
        Assert.True(fix.FileExistsInWorktree("t1", "wt-only.txt"));
        Assert.True(fix.FileExistsInWorktree("t1", "main-only.txt"));
    }

    [Fact]
    public async Task AdvanceWorktreeOntoBase_aborts_on_conflict_returns_false()
    {
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("a.txt", "ORIGINAL");
        await fix.CommitAllAsync("initial");
        await fix.SetupWorktreeAsync("t1");

        await fix.WriteInWorktreeAsync("t1", "a.txt", "WT-VERSION");
        await fix.CommitInWorktreeAsync("t1", "wt change");

        // 같은 파일을 main에서도 다르게 수정 → rebase 시 conflict
        await fix.WriteFileAsync("a.txt", "MAIN-VERSION");
        await fix.CommitAllAsync("main change");

        var result = await fix.Worktree.AdvanceWorktreeOntoBaseAsync("t1", "main");

        Assert.False(result.Success);
        Assert.Equal(MergeFailureKind.RebaseConflict, result.FailureKind);
        Assert.Contains("a.txt", result.ConflictFiles ?? new());
        // rebase --abort로 worktree가 깨끗한 상태로 복원되었는지 확인
        Assert.Equal("WT-VERSION", fix.ReadInWorktree("t1", "a.txt"));
    }

    // ─── ParseUntrackedOverwrites (머지 abort 메시지 파서) ─────────────────────

    [Fact]
    public void ParseUntrackedOverwrites_extracts_files_from_git_message()
    {
        var output =
            "error: The following untracked working tree files would be overwritten by merge:\n" +
            "\tsubtract.py\n" +
            "\tsrc/foo.py\n" +
            "Please move or remove them before you merge.\n" +
            "Aborting\n";

        var result = WorktreeService.ParseUntrackedOverwrites(output);

        Assert.Equal(2, result.Count);
        Assert.Contains("subtract.py", result);
        Assert.Contains("src/foo.py", result);
    }

    [Fact]
    public void ParseUntrackedOverwrites_returns_empty_for_unrelated_message()
    {
        var output =
            "Auto-merging file.txt\n" +
            "CONFLICT (content): Merge conflict in file.txt\n" +
            "Automatic merge failed; fix conflicts and then commit the result.\n";

        var result = WorktreeService.ParseUntrackedOverwrites(output);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseUntrackedOverwrites_returns_empty_for_empty_input()
    {
        Assert.Empty(WorktreeService.ParseUntrackedOverwrites(""));
        Assert.Empty(WorktreeService.ParseUntrackedOverwrites(null!));
    }

    [Fact]
    public void ParseUntrackedOverwrites_handles_crlf_line_endings()
    {
        var output =
            "error: The following untracked working tree files would be overwritten by merge:\r\n" +
            "\tsubtract.py\r\n" +
            "Please move or remove them before you merge.\r\n";

        var result = WorktreeService.ParseUntrackedOverwrites(output);

        Assert.Single(result);
        Assert.Equal("subtract.py", result[0]);
    }

    // ─── ParseUntrackedOverwrites Warn 로그 검증 ────────────────────────────

    [Fact]
    public void ParseUntrackedOverwrites_empty_stderr_does_not_warn()
    {
        // 빈 입력은 키워드 탐색 자체를 하지 않으므로 logger.Warn이 호출되어서는 안 된다.
        var logDir = Path.Combine(Path.GetTempPath(), $"ralph-log-{Guid.NewGuid():N}");
        string logFile;
        List<string> result;
        try
        {
            using (var logger = new RalphLogger(logDir))
            {
                result = WorktreeService.ParseUntrackedOverwrites("", logger);
                logFile = logger.LogFile;
            }
            var logContent = File.ReadAllText(logFile);
            Assert.Empty(result);
            Assert.DoesNotContain("[WARN]", logContent);
        }
        finally
        {
            try { Directory.Delete(logDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ParseUntrackedOverwrites_keyword_present_but_no_files_logs_warn()
    {
        // git이 키워드를 출력했지만 파일 목록을 추출할 수 없을 때(비표준 포맷 등)
        // 파싱 실패 Warn이 기록되어야 한다.
        var input =
            "error: The following untracked working tree files would be overwritten by merge:\n" +
            "Aborting\n";

        var logDir = Path.Combine(Path.GetTempPath(), $"ralph-log-{Guid.NewGuid():N}");
        string logFile;
        List<string> result;
        try
        {
            using (var logger = new RalphLogger(logDir))
            {
                result = WorktreeService.ParseUntrackedOverwrites(input, logger);
                logFile = logger.LogFile;
            }
            var logContent = File.ReadAllText(logFile);
            Assert.Empty(result);
            Assert.Contains("[WARN]", logContent);
            Assert.Contains("ParseUntrackedOverwrites", logContent);
        }
        finally
        {
            try { Directory.Delete(logDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ParseUntrackedOverwrites_unrelated_input_does_not_warn()
    {
        // 키워드가 없는 일반 충돌 메시지에서는 Warn이 발생하지 않아야 한다.
        var input =
            "Auto-merging file.txt\n" +
            "CONFLICT (content): Merge conflict in file.txt\n" +
            "Automatic merge failed; fix conflicts and then commit the result.\n";

        var logDir = Path.Combine(Path.GetTempPath(), $"ralph-log-{Guid.NewGuid():N}");
        string logFile;
        List<string> result;
        try
        {
            using (var logger = new RalphLogger(logDir))
            {
                result = WorktreeService.ParseUntrackedOverwrites(input, logger);
                logFile = logger.LogFile;
            }
            var logContent = File.ReadAllText(logFile);
            Assert.Empty(result);
            Assert.DoesNotContain("[WARN]", logContent);
        }
        finally
        {
            try { Directory.Delete(logDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AdvanceWorktreeOntoBase_noop_when_base_unchanged()
    {
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("a.txt", "A");
        await fix.CommitAllAsync("initial");
        await fix.SetupWorktreeAsync("t1");

        await fix.WriteInWorktreeAsync("t1", "wt.txt", "wt");
        await fix.CommitInWorktreeAsync("t1", "wt change");

        // main 이동 없음 — rebase는 이미 up-to-date로 즉시 성공
        var result = await fix.Worktree.AdvanceWorktreeOntoBaseAsync("t1", "main");

        Assert.True(result.Success);
        Assert.Equal(MergeFailureKind.None, result.FailureKind);
        Assert.True(fix.FileExistsInWorktree("t1", "wt.txt"));
    }

    // ─── BranchGuard (fix2 #4) ──────────────────────────────────────────────

    [Fact]
    public async Task BranchGuard_config_set_no_worktree_dir_user_commit_holds()
    {
        // ralphManaged config 있지만 워크트리 디렉토리가 없고 사용자 커밋이 있는 브랜치 →
        // HoldUserOwned 판정 → 삭제 보류 + 수동 삭제 안내 메시지 출력
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("seed.txt", "S");
        await fix.CommitAllAsync("initial");

        // 워크트리 생성 후 사용자 커밋 추가 (ralph 시그니처 없는 메시지)
        await fix.SetupWorktreeAsync("t1");
        await fix.WriteInWorktreeAsync("t1", "userfile.txt", "user content");
        await fix.CommitInWorktreeAsync("t1", "user's own work");

        // 워크트리 디렉토리 제거 (브랜치와 reflog는 보존)
        var wtPath = Path.Combine(fix.WorktreeBase, "t1");
        await fix.Git.RunAsync(["worktree", "remove", wtPath, "--force"], fix.RepoDir);

        // ralphManaged config 마커 설정 (A 신호)
        await fix.Git.RunAsync(
            ["config", "branch.ralph/t1.ralphManaged", "true"], fix.RepoDir);

        var logDir = Path.Combine(Path.GetTempPath(), $"ralph-log-{Guid.NewGuid():N}");
        string logFile;
        try
        {
            using (var logger = new RalphLogger(logDir))
            {
                using (fix.UseRepoCwd())
                    await fix.Worktree.CleanupWorktreeAsync("t1", logger);
                logFile = logger.LogFile;
            }

            // 브랜치는 삭제 보류 상태로 보존되어야 함
            var (showExit, _) = await fix.Git.RunAsync(
                ["show-ref", "--verify", "--quiet", "refs/heads/ralph/t1"], fix.RepoDir);
            Assert.Equal(0, showExit);

            // 수동 삭제 안내 경고 메시지 출력 확인
            var logContent = File.ReadAllText(logFile);
            Assert.Contains("[WARN]", logContent);
            Assert.Contains("ralph/t1", logContent);
            Assert.Contains("수동 삭제", logContent);
        }
        finally
        {
            try { Directory.Delete(logDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task BranchGuard_user_branch_no_config_never_deleted()
    {
        // 사용자가 직접 만든 ralph/user-test 브랜치 (config/marker 없음) →
        // NotRalphManaged 판정 → 절대 삭제 안 됨
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("seed.txt", "S");
        await fix.CommitAllAsync("initial");

        // config 없이 사용자가 직접 생성한 ralph/* 브랜치
        var (e, _) = await fix.Git.RunAsync(["branch", "ralph/user-test"], fix.RepoDir);
        Assert.Equal(0, e);

        bool ok;
        using (fix.UseRepoCwd())
            ok = await fix.Worktree.CleanupWorktreeAsync("user-test");

        Assert.True(ok);

        // 브랜치 보존 확인
        var (showExit, _) = await fix.Git.RunAsync(
            ["show-ref", "--verify", "--quiet", "refs/heads/ralph/user-test"], fix.RepoDir);
        Assert.Equal(0, showExit);
    }

    [Fact]
    public async Task BranchGuard_normal_worktree_config_and_marker_deleted()
    {
        // 정상 ralph 워크트리 (config + .ralph-marker + 워크트리 디렉토리) →
        // SafeToDelete 판정 → 가드 통과 후 브랜치 삭제됨
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("seed.txt", "S");
        await fix.CommitAllAsync("initial");
        await fix.SetupWorktreeAsync("t1");

        // ralphManaged config 설정 (A 신호)
        await fix.Git.RunAsync(
            ["config", "branch.ralph/t1.ralphManaged", "true"], fix.RepoDir);

        // .ralph-marker 파일 작성 (D 신호) — schema/task-id/branch 필드 필수
        var markerPath = Path.Combine(fix.WorktreeBase, "t1", ".ralph-marker");
        await File.WriteAllTextAsync(markerPath,
            "schema: v1\ntask-id: t1\nbranch: ralph/t1\n");

        bool ok;
        using (fix.UseRepoCwd())
            ok = await fix.Worktree.CleanupWorktreeAsync("t1");

        Assert.True(ok);

        // 브랜치 삭제 확인
        var (showExit, _) = await fix.Git.RunAsync(
            ["show-ref", "--verify", "--quiet", "refs/heads/ralph/t1"], fix.RepoDir);
        Assert.NotEqual(0, showExit);
    }

    [Fact]
    public async Task BranchGuard_marker_only_no_config_active_worktree_deleted()
    {
        // marker만 있고 config 없는 경우 — 활성 worktree 연결(B 신호)이 IsRalphManaged를
        // 통과시키고, .ralph-marker(D 신호)가 SafeToDelete를 확정 → 브랜치 삭제됨
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("seed.txt", "S");
        await fix.CommitAllAsync("initial");
        await fix.SetupWorktreeAsync("t1");  // config 없이 worktree만 생성

        // config 없이 .ralph-marker 파일만 작성 (D 신호)
        var markerPath = Path.Combine(fix.WorktreeBase, "t1", ".ralph-marker");
        await File.WriteAllTextAsync(markerPath,
            "schema: v1\ntask-id: t1\nbranch: ralph/t1\n");

        bool ok;
        using (fix.UseRepoCwd())
            ok = await fix.Worktree.CleanupWorktreeAsync("t1");

        Assert.True(ok);

        // 활성 worktree(B 신호) + marker(D 신호) 조합으로 브랜치 삭제 확인
        var (showExit, _) = await fix.Git.RunAsync(
            ["show-ref", "--verify", "--quiet", "refs/heads/ralph/t1"], fix.RepoDir);
        Assert.NotEqual(0, showExit);
    }
}
