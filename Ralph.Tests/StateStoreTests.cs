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
}
