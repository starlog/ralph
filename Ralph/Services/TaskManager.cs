using System.Text.Encodings.Web;
using System.Text.Json;
using Ralph.Models;

namespace Ralph.Services;

public class TaskManager
{
    private readonly string _filePath;
    private readonly TasksFile _data;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = RalphJsonContext.Default
    };

    public TasksFile Data => _data;
    public string FilePath => _filePath;

    public bool CommitOnComplete
        => _data.Workflow?.OnTaskComplete?.CommitChanges ?? true;

    public string CommitTemplate
        => _data.Workflow?.OnTaskComplete?.CommitMessageTemplate
           ?? "[Task #{taskId}] {taskTitle}";

    private TaskManager(string filePath, TasksFile data)
    {
        _filePath = filePath;
        _data = data;
    }

    public static async Task<TaskManager> LoadAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath);
        var data = JsonSerializer.Deserialize<TasksFile>(json, JsonOptions)
                   ?? throw new InvalidOperationException($"Failed to deserialize {filePath}");
        return new TaskManager(filePath, data);
    }

    public async Task SaveAsync()
    {
        var tmpFile = _filePath + $".tmp.{Guid.NewGuid():N}";
        try
        {
            var json = JsonSerializer.Serialize(_data, JsonOptions);

            // Validate before writing
            var verify = JsonSerializer.Deserialize<TasksFile>(json, JsonOptions);
            if (verify?.Tasks == null)
                throw new InvalidOperationException("Validation failed: tasks array missing");

            await File.WriteAllTextAsync(tmpFile, json);
            File.Move(tmpFile, _filePath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
            throw;
        }
    }

    public TaskItem? GetTask(string id)
        => _data.Tasks.FirstOrDefault(t => t.Id == id);

    public List<TaskItem> GetPendingTasks()
        => _data.Tasks.Where(t => !t.Done).ToList();

    public TaskItem? GetNextTask()
        => _data.Tasks.FirstOrDefault(t => !t.Done);

    public bool CheckDependencies(string taskId, out List<string> blockedBy)
    {
        blockedBy = [];
        var task = GetTask(taskId);
        if (task?.DependsOn is not { Count: > 0 })
            return true;

        foreach (var depId in task.DependsOn)
        {
            var dep = GetTask(depId);
            if (dep == null || !dep.Done)
                blockedBy.Add(depId);
        }

        return blockedBy.Count == 0;
    }

    public string? GetNextReadyTask()
    {
        foreach (var task in _data.Tasks.Where(t => !t.Done))
        {
            if (CheckDependencies(task.Id, out _))
                return task.Id;
        }
        return null;
    }

    public void MarkTaskDone(string taskId)
    {
        var task = GetTask(taskId)
                   ?? throw new ArgumentException($"Task '{taskId}' not found");
        task.Done = true;
    }

    public void MarkSubtaskDone(string taskId, string subtaskId)
    {
        var task = GetTask(taskId)
                   ?? throw new ArgumentException($"Task '{taskId}' not found");
        var subtask = task.Subtasks?.FirstOrDefault(s => s.Id == subtaskId)
                      ?? throw new ArgumentException($"Subtask '{subtaskId}' not found");
        subtask.Done = true;
    }

    public void ResetAll()
    {
        foreach (var task in _data.Tasks)
        {
            task.Done = false;
            if (task.Subtasks == null) continue;
            foreach (var sub in task.Subtasks)
                sub.Done = false;
        }
    }

    public int GetTaskIndex(string taskId)
    {
        for (var i = 0; i < _data.Tasks.Count; i++)
        {
            if (_data.Tasks[i].Id == taskId)
                return i + 1;
        }
        return -1;
    }
}
