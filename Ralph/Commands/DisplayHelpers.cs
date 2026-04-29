using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary>
/// 진행률·태스크 정보 출력 공용 함수. Program.cs의 ShowProgress / DisplayTask을 추출.
/// </summary>
public static class DisplayHelpers
{
    public const string Version = "1.21";

    public static void ShowProgress(TaskManager tm, RalphLogger? logger)
    {
        var total = tm.Data.Tasks.Count;
        var done = tm.Data.Tasks.Count(t => t.Done);
        var pending = tm.GetPendingTasks();
        var blocked = pending.Count(t => !tm.CheckDependencies(t.Id, out _));
        var ready = pending.Count - blocked;

        AnsiConsole.Write(new Rule($"[green]RALPH - Task Orchestrator[/] [cyan]v{Version}[/]").RuleStyle("grey"));
        AnsiConsole.MarkupLine(
            $"Total: {total} | [green]Done: {done}[/] | [yellow]Ready: {ready}[/] | [red]Blocked: {blocked}[/]");
        if (ready > 1)
            AnsiConsole.MarkupLine($"[green]{ready}개 태스크 병렬 실행 가능[/]");
        if (logger != null)
            AnsiConsole.MarkupLine($"[cyan]Log: {Markup.Escape(logger.LogFile)}[/]");
        AnsiConsole.Write(new Rule().RuleStyle("grey"));
    }

    public static void DisplayTask(TaskManager tm, string taskId)
    {
        var task = tm.GetTask(taskId)!;
        var index = tm.GetTaskIndex(taskId);
        var total = tm.Data.Tasks.Count;
        var outputFiles = task.OutputFiles is { Count: > 0 } ? string.Join(", ", task.OutputFiles) : "";
        var modifiedFiles = task.ModifiedFiles is { Count: > 0 } ? string.Join(", ", task.ModifiedFiles) : "";
        var deps = task.DependsOn is { Count: > 0 } ? string.Join(", ", task.DependsOn) : "";

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule().RuleStyle("grey"));
        AnsiConsole.MarkupLine(
            $"[yellow][[{index}/{total}]][/] [green]Task ID:[/] {Markup.Escape(task.Id)}");
        AnsiConsole.MarkupLine(
            $"[green]Phase:[/] {Markup.Escape(task.Phase ?? "-")} | [green]Category:[/] {Markup.Escape(task.Category ?? "-")}");
        AnsiConsole.MarkupLine($"[green]Title:[/] {Markup.Escape(task.Title)}");

        if (!string.IsNullOrEmpty(task.Description))
            AnsiConsole.MarkupLine($"[green]Description:[/] {Markup.Escape(task.Description)}");
        if (!string.IsNullOrEmpty(deps))
            AnsiConsole.MarkupLine($"[cyan]Depends On:[/] {Markup.Escape(deps)}");
        if (!string.IsNullOrEmpty(outputFiles))
            AnsiConsole.MarkupLine($"[cyan]Output Files:[/] {Markup.Escape(outputFiles)}");
        if (!string.IsNullOrEmpty(modifiedFiles))
            AnsiConsole.MarkupLine($"[cyan]Modified Files:[/] {Markup.Escape(modifiedFiles)}");
        if (!string.IsNullOrEmpty(task.Prompt))
            AnsiConsole.MarkupLine("[cyan]Claude Prompt:[/] (available)");

        if (task.Subtasks is { Count: > 0 })
        {
            AnsiConsole.MarkupLine("[yellow]Subtasks:[/]");
            foreach (var sub in task.Subtasks)
            {
                var check = sub.Done ? "v" : " ";
                AnsiConsole.MarkupLine(
                    $"  [[{check}]] {Markup.Escape(sub.Id)}: {Markup.Escape(sub.Title)}");
            }
        }

        AnsiConsole.Write(new Rule().RuleStyle("grey"));
        AnsiConsole.WriteLine();
    }

    public static void RequireFile(string path)
    {
        if (File.Exists(path)) return;
        AnsiConsole.MarkupLine(
            $"[red]Error: {Markup.Escape(path)} not found. Run 'ralph --plan <prd-file>' to generate it.[/]");
        Environment.Exit(1);
    }
}
