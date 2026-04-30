using Spectre.Console;

namespace Ralph.Commands;

/// <summary><c>ralph --version</c> / <c>-v</c> — ralph 버전 표시.</summary>
public sealed class VersionCommand : ICommand
{
    public Task<int> ExecuteAsync(CancellationToken ct)
    {
        AnsiConsole.MarkupLine($"ralph v{DisplayHelpers.Version}");
        return Task.FromResult(0);
    }
}
