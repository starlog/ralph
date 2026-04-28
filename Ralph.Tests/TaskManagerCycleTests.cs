using Ralph.Models;
using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

public class TaskManagerCycleTests
{
    private static async Task<TaskManager> LoadFromJsonAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ralph-cycle-test-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        try
        {
            return await TaskManager.LoadAsync(path);
        }
        finally
        {
            // 호출자가 인스턴스를 받은 뒤에는 path를 더 이상 안 읽으므로 즉시 삭제 가능 —
            // 하지만 ReloadAsync 호출 가능성 대비 테스트 종료 시점까지 둔다.
        }
    }

    [Fact]
    public async Task No_dependencies_means_no_cycle()
    {
        var tm = await LoadFromJsonAsync("""
        {"tasks":[
          {"id":"a","title":"A","done":false},
          {"id":"b","title":"B","done":false}
        ]}
        """);
        Assert.False(tm.HasCycle(out var cycle));
        Assert.Empty(cycle);
    }

    [Fact]
    public async Task Linear_chain_has_no_cycle()
    {
        var tm = await LoadFromJsonAsync("""
        {"tasks":[
          {"id":"a","title":"A","done":false},
          {"id":"b","title":"B","done":false,"dependsOn":["a"]},
          {"id":"c","title":"C","done":false,"dependsOn":["b"]}
        ]}
        """);
        Assert.False(tm.HasCycle(out _));
    }

    [Fact]
    public async Task Two_node_cycle_is_detected()
    {
        var tm = await LoadFromJsonAsync("""
        {"tasks":[
          {"id":"a","title":"A","done":false,"dependsOn":["b"]},
          {"id":"b","title":"B","done":false,"dependsOn":["a"]}
        ]}
        """);
        Assert.True(tm.HasCycle(out var cycle));
        Assert.Contains("a", cycle);
        Assert.Contains("b", cycle);
    }
}
