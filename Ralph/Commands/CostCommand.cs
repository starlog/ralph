namespace Ralph.Commands;

/// <summary><c>ralph --cost</c> — 누적 토큰 사용량 + 추정 USD 비용.</summary>
public sealed class CostCommand : ICommand
{
    private readonly CommandContext _ctx;

    public CostCommand(CommandContext ctx)
    {
        _ctx = ctx;
    }

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        await _ctx.Cost.PrintSummaryAsync(ct);
        return 0;
    }
}
