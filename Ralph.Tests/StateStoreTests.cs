using System.Text.Json;
using Ralph.Models;
using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

public class StateStoreTests
{
    private static string TempDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ralph-state-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public async Task Mark_done_persists_atomically_and_reload_sees_it()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "state.json");
        var s = await StateStore.OpenAsync(path);

        await s.MarkDoneAsync("a");

        Assert.True(File.Exists(path));
        Assert.True(s.IsDone("a"));

        var s2 = await StateStore.OpenAsync(path);
        Assert.True(s2.IsDone("a"));
    }

    [Fact]
    public async Task Subtask_done_is_independent_of_task_done()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "state.json");
        var s = await StateStore.OpenAsync(path);

        await s.MarkSubtaskDoneAsync("alpha", "sub-1");

        Assert.True(s.IsSubtaskDone("alpha", "sub-1"));
        Assert.False(s.IsSubtaskDone("alpha", "sub-2"));
        Assert.False(s.IsDone("alpha"));
    }

    [Fact]
    public async Task ResetAll_clears_state_but_not_caller_data()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "state.json");
        var s = await StateStore.OpenAsync(path);
        await s.MarkDoneAsync("a");
        await s.MarkDoneAsync("b");

        await s.ResetAllAsync();

        Assert.False(s.IsDone("a"));
        Assert.False(s.IsDone("b"));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Concurrent_mark_done_does_not_lose_writes()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "state.json");
        var s = await StateStore.OpenAsync(path);

        var ids = Enumerable.Range(0, 20).Select(i => $"task-{i}").ToList();
        await Task.WhenAll(ids.Select(id => s.MarkDoneAsync(id)));

        foreach (var id in ids)
            Assert.True(s.IsDone(id));

        // 디스크 상태도 일관: 다시 열면 모두 보임
        var s2 = await StateStore.OpenAsync(path);
        foreach (var id in ids)
            Assert.True(s2.IsDone(id));
    }

    [Fact]
    public async Task Default_path_resolves_under_ralph_logs_next_to_tasks_file()
    {
        var dir = TempDir();
        var tasksFile = Path.Combine(dir, "tasks.json");
        await File.WriteAllTextAsync(tasksFile, "{}");

        var statePath = StateStore.DefaultPathFor(tasksFile);
        Assert.Equal(Path.Combine(dir, ".ralph-logs", "state.json"), statePath);
    }

    /// <summary>
    /// Legacy v1 tasks.json (`done:true` 포함) 첫 로드 시:
    /// - state.json이 생성되고 done 비트가 옮겨진다
    /// - tasks.json이 done 키 없이 재저장된다 (idempotent)
    /// </summary>
    [Fact]
    public async Task Legacy_done_in_tasks_json_is_migrated_to_state_on_first_load()
    {
        var dir = TempDir();
        var tasksFile = Path.Combine(dir, "tasks.json");
        var legacy = """
        {
          "tasks": [
            {"id":"a","title":"A","done":true,"prompt":"p"},
            {"id":"b","title":"B","done":false,"prompt":"p","subtasks":[
              {"id":"sub-1","title":"S1","done":true},
              {"id":"sub-2","title":"S2","done":false}
            ]}
          ]
        }
        """;
        await File.WriteAllTextAsync(tasksFile, legacy);

        var tm = await TaskManager.LoadAsync(tasksFile);

        // state는 이관됨
        Assert.True(tm.IsDone("a"));
        Assert.False(tm.IsDone("b"));
        Assert.True(tm.IsSubtaskDone("b", "sub-1"));
        Assert.False(tm.IsSubtaskDone("b", "sub-2"));

        // tasks.json은 done 키 없이 재저장됨
        var rewritten = await File.ReadAllTextAsync(tasksFile);
        Assert.DoesNotContain("\"done\"", rewritten);

        // state.json 파일이 디스크에 생성됨
        var statePath = StateStore.DefaultPathFor(tasksFile);
        Assert.True(File.Exists(statePath));

        // 재로드 시 마이그레이션이 다시 발생하지 않음 (idempotent)
        var tm2 = await TaskManager.LoadAsync(tasksFile);
        Assert.True(tm2.IsDone("a"));
        Assert.True(tm2.IsSubtaskDone("b", "sub-1"));
    }

    /// <summary>
    /// 레거시 done 키가 모두 false인 tasks.json도 재저장 후 done 키가 사라져야 한다.
    /// </summary>
    [Fact]
    public async Task Legacy_done_false_only_is_still_stripped_on_first_load()
    {
        var dir = TempDir();
        var tasksFile = Path.Combine(dir, "tasks.json");
        await File.WriteAllTextAsync(tasksFile, """
        {
          "tasks": [
            {"id":"a","title":"A","done":false,"prompt":"p"}
          ]
        }
        """);

        await TaskManager.LoadAsync(tasksFile);

        var rewritten = await File.ReadAllTextAsync(tasksFile);
        Assert.DoesNotContain("\"done\"", rewritten);
    }

    /// <summary>
    /// done 키가 전혀 없는 새 tasks.json은 마이그레이션을 트리거하지 않고 그대로 둔다.
    /// (재저장으로 발생하는 무의미한 변경 방지)
    /// </summary>
    [Fact]
    public async Task No_legacy_done_means_tasks_json_is_left_untouched()
    {
        var dir = TempDir();
        var tasksFile = Path.Combine(dir, "tasks.json");
        var content = """
        {
          "tasks": [
            {"id":"a","title":"A","prompt":"p"}
          ]
        }
        """;
        await File.WriteAllTextAsync(tasksFile, content);
        var beforeMtime = File.GetLastWriteTimeUtc(tasksFile);

        await Task.Delay(50);
        await TaskManager.LoadAsync(tasksFile);

        var afterMtime = File.GetLastWriteTimeUtc(tasksFile);
        Assert.Equal(beforeMtime, afterMtime);
    }

    // ─── 재시도 로직 단위 테스트 ────────────────────────────────────────────────

    /// <summary>
    /// IOException이 maxRetries(2)를 초과해 모든 재시도에서 실패하면 호출자에게 전파된다.
    /// state.json 경로를 디렉토리로 만들어 File.Move(tmp → state.json)를 영구 실패시킨다.
    /// </summary>
    [Fact]
    public async Task MarkDone_propagates_IOException_when_all_retries_exhausted()
    {
        var dir = TempDir();
        var statePath = Path.Combine(dir, "state.json");

        // state.json 경로에 디렉토리를 생성 → File.Move(tmp → state.json) 영구 실패
        Directory.CreateDirectory(statePath);

        var s = await StateStore.OpenAsync(statePath);

        // 모든 재시도(attempt 0, 1, 2) 후 IOException이 호출자에게 전파되어야 한다
        await Assert.ThrowsAnyAsync<IOException>(() => s.MarkDoneAsync("task1"));

        // 디스크 상태: statePath는 여전히 디렉토리 → File.Exists(dir)=false → 새 StateStore는 빈 상태
        var freshState = await StateStore.OpenAsync(statePath);
        Assert.False(freshState.IsDone("task1"), "disk 쓰기 실패 → done=false여야 한다");
    }

    /// <summary>
    /// 첫 번째 시도에서 IOException이 발생하더라도 재시도(100ms 후)에서 성공하면 done 처리가 완료된다.
    /// state.json 경로를 디렉토리로 막은 뒤 40ms 후 제거 → 재시도 시 쓰기가 가능해진다.
    /// </summary>
    [Fact]
    public async Task MarkDone_succeeds_on_retry_when_first_io_attempt_fails_transiently()
    {
        var dir = TempDir();
        var logDir = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logDir);
        var statePath = Path.Combine(logDir, "state.json");

        // 첫 번째 쓰기를 막기 위해 state.json 경로에 디렉토리 생성
        Directory.CreateDirectory(statePath);

        var s = await StateStore.OpenAsync(statePath);

        // 40ms 후 디렉토리를 제거 → 재시도(100ms 후)에서 File.Move가 성공함
        _ = Task.Delay(40).ContinueWith(_ =>
        {
            try { Directory.Delete(statePath); } catch { /* best-effort */ }
        });

        // IOException 없이 성공해야 한다 (첫 시도 실패 → 재시도 성공)
        await s.MarkDoneAsync("task1");

        Assert.True(s.IsDone("task1"), "재시도 성공 후 in-memory done=true여야 한다");

        // 재시도에서 디스크에도 기록되었는지 확인
        var freshState = await StateStore.OpenAsync(statePath);
        Assert.True(freshState.IsDone("task1"), "재시도 성공 후 disk state에도 done=true여야 한다");
    }
}
