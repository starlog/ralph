using Spectre.Console;

namespace Ralph.Commands;

/// <summary>외부 CLI(claude, git) 존재 검사. 미설치 시 즉시 종료.</summary>
public static class DependencyChecker
{
    public static void Check(string name, string displayName, string url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = name,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch (Exception)
        {
            AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(displayName)} is required but not found.[/]");
            AnsiConsole.MarkupLine($"Install from: {Markup.Escape(url)}");
            Environment.Exit(1);
        }
    }
}
