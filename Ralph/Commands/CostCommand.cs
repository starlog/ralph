using Ralph.Services;

namespace Ralph.Commands;

/// <summary><c>ralph --cost</c> — 누적 토큰 사용량 + 추정 USD 비용.</summary>
public sealed class CostCommand : ICommand
{
    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var tracker = new CostTracker();
        await tracker.PrintSummaryAsync(ct);
        return 0;
    }
}
