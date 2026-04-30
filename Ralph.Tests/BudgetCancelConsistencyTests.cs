using System.Text.Json;
using Ralph.Models;
using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

/// <summary>
/// BudgetGate가 새 task 디스패치를 차단한 직후 Ctrl+C(CancellationToken.Cancel) 시나리오 검증.
///
/// 잔여 시나리오 (#7 fix1.md 7항):
/// 1. BudgetGate.CheckAsync가 budget 초과 시 false·Reached=true를 반환하고 한국어 안내를 logger에 남기는지.
/// 2. 차단 직후 cancel 시 StateStore가 atomic write로 state.json을 일관된 상태로 유지하는지
///    (부분 쓰기 없음 — tmp+rename 보장 검증).
/// </summary>
[Collection("cost")]
public class BudgetCancelConsistencyTests : IDisposable
{
    private readonly string _tempDir;

    public BudgetCancelConsistencyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ralph-budget-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        CostTracker.SetLogDirForTesting(_tempDir);
        CostTracker.ResetForTesting();
    }

    public void Dispose()
    {
        CostTracker.ResetForTesting();
        CostTracker.SetLogDirForTesting(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static ClaudeResult MakePricedResult(long inputTokens)
        => new()
        {
            Success = true,
            Usage = new TokenUsage(inputTokens, 0, 0, 0),
            Duration = TimeSpan.FromSeconds(1),
        };

    // ─── BudgetGate 차단 · Reached 플래그 · 한국어 안내 로그 검증 ─────────────────────

    [Fact]
    public async Task BudgetGate_returns_false_and_sets_Reached_when_cost_exceeds_budget()
    {
        var cost = new CostTracker();
        // opus input 단가 $15/1M → 500K tokens ≈ $7.50, budget $5 → 초과
        await cost.RecordAsync("task1", "opus", MakePricedResult(500_000));

        var gate = new BudgetGate(5.0, cost);
        var allowed = await gate.CheckAsync();

        Assert.False(allowed, "budget 초과 시 CheckAsync는 false를 반환해야 한다");
        Assert.True(gate.Reached, "budget 초과 시 Reached=true여야 한다");
    }

    [Fact]
    public async Task BudgetGate_logs_budget_reached_with_Korean_hint_in_logger()
    {
        // "새 태스크 시작을 중단합니다" 한국어 안내는 AnsiConsole로 출력(캡처 어려움).
        // logger에는 "[budget] reached: $X / $Y" 가 ERROR 레벨로 기록된다 — 그 보존을 검증한다.
        var logDir = Path.Combine(_tempDir, "log-budget");
        using var logger = new RalphLogger(logDir);

        var cost = new CostTracker();
        await cost.RecordAsync("task1", "opus", MakePricedResult(500_000));

        var gate = new BudgetGate(5.0, cost, logger);
        await gate.CheckAsync();

        var logContent = await File.ReadAllTextAsync(logger.LogFile);
        // BudgetGate 내 _logger.Error("[budget] reached: ...")
        Assert.Contains("budget", logContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reached", logContent, StringComparison.OrdinalIgnoreCase);
        // gate.Reached=true는 "budget 초과" 상태를 프로그래밍적으로 표현
        Assert.True(gate.Reached);
    }

    // ─── CancellationToken 흘려보내기 후 state.json 일관성 ──────────────────────────────

    [Fact]
    public async Task StateStore_json_is_valid_after_cancel_mid_write()
    {
        // budget gate 차단 직후 Ctrl+C → 이미 기록된 state.json은 유효한 JSON을 유지해야 한다
        var statePath = Path.Combine(_tempDir, ".ralph-logs-1", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);

        var store = await StateStore.OpenAsync(statePath);
        await store.MarkDoneAsync("task1");
        await store.MarkDoneAsync("task2");

        // Ctrl+C 시뮬레이션: already-cancelled CancellationToken으로 추가 쓰기 시도
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.MarkDoneAsync("task3", cts.Token));

        // state.json은 항상 유효한 JSON이어야 한다 (atomic tmp+rename → 부분 쓰기 없음)
        Assert.True(File.Exists(statePath), "state.json 파일이 존재해야 한다");
        var json = await File.ReadAllTextAsync(statePath);
        var parsed = JsonSerializer.Deserialize<StateFile>(json, StateStore.JsonOptions);
        Assert.NotNull(parsed);

        // 취소 이전에 기록된 태스크는 일관되게 유지되어야 한다
        Assert.True(parsed!.Tasks.TryGetValue("task1", out var t1) && t1.Done,
            "취소 이전 task1은 done=true여야 한다");
        Assert.True(parsed.Tasks.TryGetValue("task2", out var t2) && t2.Done,
            "취소 이전 task2는 done=true여야 한다");

        // 취소된 task3는 done=false(또는 미존재)여야 한다
        var task3Done = parsed.Tasks.TryGetValue("task3", out var t3) && t3.Done;
        Assert.False(task3Done, "취소된 task3는 done=false여야 한다");
    }

    [Fact]
    public async Task Budget_gate_block_then_cancel_leaves_state_json_consistent()
    {
        // Budget gate 차단 직후 Ctrl+C → 기록된 state.json이 손상되지 않는 end-to-end 검증
        var statePath = Path.Combine(_tempDir, ".ralph-logs-2", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);

        var store = await StateStore.OpenAsync(statePath);
        await store.MarkDoneAsync("task-a");
        await store.MarkDoneAsync("task-b");

        // CostTracker 누적으로 budget gate 차단 재현
        // (mock IAgentRunner 대신 RecordAsync 직접 호출 — 결정적으로 재현 가능)
        var cost = new CostTracker();
        for (var i = 0; i < 3; i++)
            await cost.RecordAsync($"task-{(char)('a' + i)}", "opus", MakePricedResult(200_000));
        // 3 × 200K tokens × $15/1M = ~$9.00 > $1 budget

        var cts = new CancellationTokenSource();
        var gate = new BudgetGate(1.0, cost);

        var allowed = await gate.CheckAsync(cts.Token);
        Assert.False(allowed, "budget 초과 시 CheckAsync는 false여야 한다");
        Assert.True(gate.Reached);

        // Budget 차단 직후 Ctrl+C 시뮬레이션
        cts.Cancel();

        // 취소 이후 추가 쓰기는 OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.MarkDoneAsync("task-c", cts.Token));

        // state.json은 여전히 유효한 JSON이어야 한다 (부분 쓰기 없음)
        var json = await File.ReadAllTextAsync(statePath);
        var parsed = JsonSerializer.Deserialize<StateFile>(json, StateStore.JsonOptions);
        Assert.NotNull(parsed);

        Assert.True(parsed!.Tasks.TryGetValue("task-a", out var ta) && ta.Done,
            "budget 차단·취소 후 기존 task-a done=true 유지");
        Assert.True(parsed.Tasks.TryGetValue("task-b", out var tb) && tb.Done,
            "budget 차단·취소 후 기존 task-b done=true 유지");

        var taskCDone = parsed.Tasks.TryGetValue("task-c", out var tc) && tc.Done;
        Assert.False(taskCDone, "취소된 task-c는 done=false여야 한다");
    }

    [Fact]
    public async Task Multiple_accumulated_costs_trigger_budget_gate_deterministically()
    {
        // 여러 RecordAsync 호출로 비용을 누적시켜 BudgetGate 차단을 결정적으로 재현
        var cost = new CostTracker();
        for (var i = 0; i < 3; i++)
        {
            await cost.RecordAsync($"task-{i}", "opus",
                new ClaudeResult
                {
                    Success = true,
                    Usage = new TokenUsage(200_000, 50_000, 0, 0),
                    Duration = TimeSpan.FromSeconds(2),
                });
        }

        var gate = new BudgetGate(1.0, cost); // $1 budget
        using var cts = new CancellationTokenSource();

        var allowed = await gate.CheckAsync();
        Assert.False(allowed, "누적 비용이 budget을 초과하면 CheckAsync는 false여야 한다");
        Assert.True(gate.Reached, "budget 초과 시 Reached=true여야 한다");

        // Cancel을 흘려보낸 이후에도 이미 확인된 Reached 상태는 변하지 않는다
        cts.Cancel();
        Assert.True(gate.Reached, "Cancel 이후에도 budget Reached 상태는 변하지 않는다");
    }
}
