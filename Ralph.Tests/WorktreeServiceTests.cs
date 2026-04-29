using Xunit;

namespace Ralph.Tests;

/// <summary>
/// WorktreeService의 머지 직전 단계(NormalizeTasksJson, ValidateModifiedFiles,
/// AdvanceWorktreeOntoBase)를 실제 git fixture로 검증. 각 테스트는 unique temp dir의
/// repo + worktree에서 격리되어 병렬 실행 가능.
/// </summary>
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

        var ok = await fix.Worktree.AdvanceWorktreeOntoBaseAsync("t1", "main");

        Assert.True(ok);
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

        var ok = await fix.Worktree.AdvanceWorktreeOntoBaseAsync("t1", "main");

        Assert.False(ok);
        // rebase --abort로 worktree가 깨끗한 상태로 복원되었는지 확인
        Assert.Equal("WT-VERSION", fix.ReadInWorktree("t1", "a.txt"));
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
        var ok = await fix.Worktree.AdvanceWorktreeOntoBaseAsync("t1", "main");

        Assert.True(ok);
        Assert.True(fix.FileExistsInWorktree("t1", "wt.txt"));
    }
}
