using Spectre.Console;

namespace Ralph.Services;

/// <summary>
/// 누적 비용 임계값(--budget-usd) 게이트. 80% 도달 시 1회 경고, 100% 도달 시 차단.
/// 단일 인스턴스를 RunAutoLoop와 ParallelExecutor 양쪽이 공유해 메시지/플래그를 통일한다.
/// budget이 null/0/음수면 모든 호출이 통과(true)된다.
/// </summary>
public class BudgetGate
{
    private readonly double? _budgetUsd;
    private readonly CostTracker _cost;
    private readonly RalphLogger? _logger;
    private bool _warned;
    private bool _reached;

    public BudgetGate(double? budgetUsd, CostTracker cost, RalphLogger? logger = null)
    {
        _budgetUsd = budgetUsd;
        _cost = cost;
        _logger = logger;
    }

    /// <summary>budget(USD) 임계값 도달로 새 dispatch가 차단되었는지 여부.</summary>
    public bool Reached => _reached;

    /// <summary>새 task/batch dispatch 직전 호출. true=계속 진행, false=차단.</summary>
    public async Task<bool> CheckAsync(CancellationToken ct = default)
    {
        if (_budgetUsd is not { } budget || budget <= 0.0) return true;

        var total = await _cost.GetTotalUsdAsync(ct);

        if (!_warned && total >= budget * 0.8)
        {
            _warned = true;
            var pct = total / budget * 100.0;
            AnsiConsole.MarkupLine(
                $"[yellow]⚠ 예산 80% 도달[/] (${total:F2} / ${budget:F2}, {pct:F0}%)");
            _logger?.Warn($"[budget] 80% threshold hit: ${total:F4} / ${budget:F4}");
        }

        if (total >= budget)
        {
            _reached = true;
            AnsiConsole.MarkupLine(
                $"[red]✗ budget reached[/] (${total:F2} / ${budget:F2}). " +
                "새 태스크 시작을 중단합니다. 진행 중 태스크는 완료까지 대기합니다.");
            AnsiConsole.MarkupLine(
                "[dim]다음 실행: [cyan]ralph --run --budget-usd <larger>[/] 또는 " +
                "[cyan]ralph --run[/] (예산 제한 없음).[/]");
            _logger?.Error($"[budget] reached: ${total:F4} / ${budget:F4}");
            return false;
        }

        return true;
    }
}
