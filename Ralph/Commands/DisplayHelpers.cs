using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary>
/// 진행률·태스크 정보 출력 공용 함수. Program.cs의 ShowProgress / DisplayTask을 추출.
/// </summary>
public static class DisplayHelpers
{
    public const string Version = "1.45";

    /// <summary>
    /// 세션 시작 시 한 번 출력하는 ralph 버전 배너. 이후의 Model/그래프 스캔/실행 모드/
    /// 진행률 라인이 모두 이 배너 아래에 모이도록, 배너는 더 이상 <see cref="ShowProgress"/>가
    /// 직접 그리지 않는다 (ShowProgress는 SequentialRunner 루프 안에서 반복 호출되기 때문).
    /// </summary>
    public static void ShowBanner()
    {
        AnsiConsole.Write(new Rule($"[green]RALPH - Task Orchestrator[/] [cyan]v{Version}[/]").RuleStyle("grey"));
    }

    public static void ShowProgress(TaskManager tm, RalphLogger logger)
    {
        var total = tm.Data.Tasks.Count;
        var done = tm.Data.Tasks.Count(t => tm.IsDone(t.Id));
        var pending = tm.GetPendingTasks();
        var blocked = pending.Count(t => !tm.CheckDependencies(t.Id, out _));
        var ready = pending.Count - blocked;

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
                var check = tm.IsSubtaskDone(taskId, sub.Id) ? "v" : " ";
                AnsiConsole.MarkupLine(
                    $"  [[{check}]] {Markup.Escape(sub.Id)}: {Markup.Escape(sub.Title)}");
            }
        }

        AnsiConsole.Write(new Rule().RuleStyle("grey"));
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// 모델명을 Spectre 마크업으로 강조해서 반환한다 (Option B 팔레트).
    ///   sonnet → bold sky-blue (#6cb6ff), opus → bold amber-gold (#d4a017).
    /// 알 수 없는 값/빈 문자열은 escape만 한 평문 반환 (잘못된 마크업 출력 방지).
    /// "Model:" 같은 헤더는 호출자가 직접 그린다 — 이 함수는 모델명만 책임진다.
    /// </summary>
    public static string FormatModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "[dim]-[/]";
        return model.Trim().ToLowerInvariant() switch
        {
            "sonnet" => "[bold #6cb6ff]sonnet[/]",
            "opus"   => "[bold #d4a017]opus[/]",
            _        => Markup.Escape(model),
        };
    }

    /// <summary>
    /// "opus: N / sonnet: M" 같은 breakdown 문자열을 두 모델명만 컬러 강조해 만든다.
    /// 라벨/숫자/구분자는 dim 톤을 유지해 모델명이 두드러지게 한다.
    /// </summary>
    public static string FormatModelBreakdown(int opusCount, int sonnetCount, int unsetCount, string? unsetSuffix = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("[dim]([/]");
        sb.Append("[bold #d4a017]opus[/]");
        sb.Append($"[dim]: {opusCount} / [/]");
        sb.Append("[bold #6cb6ff]sonnet[/]");
        sb.Append($"[dim]: {sonnetCount}[/]");
        if (unsetCount > 0)
        {
            sb.Append($"[dim] / unset: {unsetCount}[/]");
            if (!string.IsNullOrEmpty(unsetSuffix))
                sb.Append($"[dim] {Markup.Escape(unsetSuffix)}[/]");
        }
        sb.Append("[dim])[/]");
        return sb.ToString();
    }

    public static void RequireFile(string path)
    {
        if (File.Exists(path)) return;
        AnsiConsole.MarkupLine(
            $"[red]Error: {Markup.Escape(path)} not found. Run 'ralph --plan <prd-file>' to generate it.[/]");
        Environment.Exit(1);
    }
}
