using Ralph.Services;

namespace Ralph.Commands;

/// <summary><c>ralph --graph</c> — ASCII 의존성 그래프.</summary>
public sealed class GraphCommand : ICommand
{
    private readonly CommandContext _ctx;

    public GraphCommand(CommandContext ctx) => _ctx = ctx;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        DisplayHelpers.RequireFile(_ctx.TasksFile);
        var tm = await TaskManager.LoadAsync(_ctx.TasksFile);
        var renderer = new GraphRenderer(tm);
        renderer.RenderToConsole();
        return 0;
    }
}
