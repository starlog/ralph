using System.Collections.Concurrent;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Ralph.Services;

public enum TaskProgressStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Merging,
}

public class TaskProgressEntry
{
    public string TaskId { get; init; } = "";
    public string Title { get; init; } = "";
    public TaskProgressStatus Status { get; set; } = TaskProgressStatus.Pending;
    public Stopwatch Stopwatch { get; } = new();
    public string? LogFile { get; set; }
    public long OutputBytes { get; set; }
}

public class TaskProgressTracker
{
    private readonly ConcurrentDictionary<string, TaskProgressEntry> _entries = new();

    public void Register(string taskId, string title, string? logFile = null)
    {
        _entries[taskId] = new TaskProgressEntry
        {
            TaskId = taskId,
            Title = title,
            LogFile = logFile,
        };
    }

    public void UpdateStatus(string taskId, TaskProgressStatus status)
    {
        if (!_entries.TryGetValue(taskId, out var entry)) return;

        entry.Status = status;
        if (status == TaskProgressStatus.Running && !entry.Stopwatch.IsRunning)
            entry.Stopwatch.Start();
        else if (status is TaskProgressStatus.Completed or TaskProgressStatus.Failed)
            entry.Stopwatch.Stop();
    }

    public void UpdateOutputSize(string taskId)
    {
        if (!_entries.TryGetValue(taskId, out var entry)) return;
        if (entry.LogFile == null) return;

        try
        {
            var fi = new FileInfo(entry.LogFile);
            if (fi.Exists)
                entry.OutputBytes = fi.Length;
        }
        catch
        {
            // best effort
        }
    }

    public void RefreshAllOutputSizes()
    {
        foreach (var taskId in _entries.Keys)
            UpdateOutputSize(taskId);
    }

    public Table BuildTable()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold blue]Parallel Execution[/]")
            .AddColumn("[bold]Task ID[/]")
            .AddColumn("[bold]Status[/]")
            .AddColumn(new TableColumn("[bold]Elapsed[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Output[/]").RightAligned())
            .AddColumn("[bold]Log File[/]");

        foreach (var entry in _entries.Values.OrderBy(e => e.TaskId))
        {
            var statusMarkup = entry.Status switch
            {
                TaskProgressStatus.Pending => "[dim]Pending[/]",
                TaskProgressStatus.Running => "[cyan]Running...[/]",
                TaskProgressStatus.Completed => "[green]Completed[/]",
                TaskProgressStatus.Failed => "[red]Failed[/]",
                TaskProgressStatus.Merging => "[yellow]Merging[/]",
                _ => "[dim]Unknown[/]",
            };

            var elapsed = entry.Stopwatch.Elapsed;
            var elapsedStr = elapsed.TotalMinutes >= 1
                ? $"{elapsed.Minutes}m {elapsed.Seconds}s"
                : $"{elapsed.Seconds}s";

            var outputStr = FormatBytes(entry.OutputBytes);
            var logFile = entry.LogFile != null ? Path.GetFileName(entry.LogFile) : "-";

            table.AddRow(
                Markup.Escape(entry.TaskId),
                statusMarkup,
                elapsedStr,
                outputStr,
                Markup.Escape(logFile));
        }

        return table;
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            0 => "-",
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes / (1024.0 * 1024.0):F1} MB",
        };
    }
}
