using System.Text.Encodings.Web;
using System.Text.Json;
using Ralph.Models;

namespace Ralph.Services;

public class TaskManager
{
    private readonly string _filePath;
    private TasksFile _data;

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

    public ParallelSettings ParallelConfig
        => _data.Workflow?.Parallel ?? new ParallelSettings();

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

    public async Task ReloadAsync()
    {
        var json = await File.ReadAllTextAsync(_filePath);
        _data = JsonSerializer.Deserialize<TasksFile>(json, JsonOptions)
                ?? throw new InvalidOperationException($"Failed to deserialize {_filePath}");
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

    /// <summary>
    /// 의존성이 모두 충족된 모든 pending 태스크를 반환합니다.
    /// </summary>
    public List<string> GetAllReadyTasks()
    {
        return _data.Tasks
            .Where(t => !t.Done && CheckDependencies(t.Id, out _))
            .Select(t => t.Id)
            .ToList();
    }

    /// <summary>
    /// ready 태스크들을 파일 충돌이 없는 배치로 그룹화합니다.
    /// 같은 파일을 수정하는 태스크는 서로 다른 배치에 배치됩니다.
    /// </summary>
    public List<List<string>> GetParallelBatches()
    {
        var readyTasks = GetAllReadyTasks();
        var batches = new List<List<string>>();
        var scheduled = new HashSet<string>();

        while (scheduled.Count < readyTasks.Count)
        {
            var batch = new List<string>();
            var batchFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var taskId in readyTasks.Where(t => !scheduled.Contains(t)))
            {
                var task = GetTask(taskId)!;
                var taskFiles = GetTaskFiles(task);

                // 파일 충돌 검사: 이 배치의 다른 태스크와 파일이 겹치지 않으면 추가
                if (taskFiles.Count == 0 || !taskFiles.Any(f => batchFiles.Contains(f)))
                {
                    batch.Add(taskId);
                    batchFiles.UnionWith(taskFiles);
                }
            }

            if (batch.Count == 0)
                break; // 무한루프 방지

            batches.Add(batch);
            scheduled.UnionWith(batch);
        }

        return batches;
    }

    /// <summary>
    /// 태스크가 수정할 파일 목록을 반환합니다. (outputFiles + modifiedFiles 통합)
    /// </summary>
    private static HashSet<string> GetTaskFiles(TaskItem task)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (task.OutputFiles is { Count: > 0 })
            files.UnionWith(task.OutputFiles);
        if (task.ModifiedFiles is { Count: > 0 })
            files.UnionWith(task.ModifiedFiles);
        return files;
    }

    /// <summary>
    /// 의존성 그래프에 순환 참조가 있는지 검사합니다. (Kahn's algorithm)
    /// </summary>
    public bool HasCycle(out List<string> cycle)
    {
        cycle = [];
        var inDegree = new Dictionary<string, int>();
        var adj = new Dictionary<string, List<string>>();

        foreach (var task in _data.Tasks)
        {
            inDegree.TryAdd(task.Id, 0);
            adj.TryAdd(task.Id, []);
        }

        foreach (var task in _data.Tasks)
        {
            if (task.DependsOn is not { Count: > 0 }) continue;
            foreach (var dep in task.DependsOn)
            {
                if (!adj.ContainsKey(dep)) continue;
                adj[dep].Add(task.Id);
                inDegree[task.Id] = inDegree.GetValueOrDefault(task.Id) + 1;
            }
        }

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var visited = 0;

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            visited++;
            foreach (var neighbor in adj[node])
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                    queue.Enqueue(neighbor);
            }
        }

        if (visited == _data.Tasks.Count)
            return false;

        // 순환에 포함된 노드 찾기
        cycle = inDegree.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
        return true;
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

    /// <summary>
    /// Kahn's algorithm으로 전체 태스크를 위상 정렬 레이어별로 그룹화합니다.
    /// 각 레이어는 동시 실행 가능한 태스크 그룹입니다.
    /// </summary>
    public List<List<string>> ComputeTopologicalLayers()
    {
        var inDegree = new Dictionary<string, int>();
        var adj = new Dictionary<string, List<string>>();

        foreach (var task in _data.Tasks)
        {
            inDegree.TryAdd(task.Id, 0);
            adj.TryAdd(task.Id, []);
        }

        foreach (var task in _data.Tasks)
        {
            if (task.DependsOn is not { Count: > 0 }) continue;
            foreach (var dep in task.DependsOn)
            {
                if (!adj.ContainsKey(dep)) continue;
                adj[dep].Add(task.Id);
                inDegree[task.Id] = inDegree.GetValueOrDefault(task.Id) + 1;
            }
        }

        var layers = new List<List<string>>();
        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));

        while (queue.Count > 0)
        {
            var layer = new List<string>();
            var nextQueue = new Queue<string>();

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                layer.Add(node);
                foreach (var neighbor in adj[node])
                {
                    inDegree[neighbor]--;
                    if (inDegree[neighbor] == 0)
                        nextQueue.Enqueue(neighbor);
                }
            }

            if (layer.Count > 0)
                layers.Add(layer);
            queue = nextQueue;
        }

        return layers;
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
