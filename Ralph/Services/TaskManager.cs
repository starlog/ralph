using System.Text.Encodings.Web;
using System.Text.Json;
using Ralph.Models;

namespace Ralph.Services;

public class TaskManager
{
    private readonly string _filePath;
    private readonly StateStore _state;
    private TasksFile _data;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = RalphJsonContext.Default
    };

    public TasksFile Data => _data;
    public string FilePath => _filePath;

    /// <summary>
    /// Mutable progress(done) 비트는 모두 여기에 보관된다. tasks.json은 spec only.
    /// </summary>
    public StateStore State => _state;

    public bool CommitOnComplete
        => _data.Workflow?.OnTaskComplete?.CommitChanges ?? true;

    public string CommitTemplate
        => _data.Workflow?.OnTaskComplete?.CommitMessageTemplate
           ?? "[Task #{taskId}] {taskTitle}";

    public ParallelSettings ParallelConfig
        => _data.Workflow?.Parallel ?? new ParallelSettings();

    private TaskManager(string filePath, TasksFile data, StateStore state)
    {
        _filePath = filePath;
        _data = data;
        _state = state;
    }

    /// <summary>
    /// tasks.json을 로드한다. 같은 디렉토리 산하의 `.ralph-logs/state.json`을 함께 연다.
    /// 첫 로드 시 legacy tasks.json(`done` 키 포함)이 발견되면 자동으로 state.json으로 이관하고
    /// tasks.json을 done 키 없이 재저장한다 (idempotent).
    /// </summary>
    public static async Task<TaskManager> LoadAsync(string filePath, StateStore? state = null, CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(filePath, ct);
        var data = JsonSerializer.Deserialize<TasksFile>(json, JsonOptions)
                   ?? throw new InvalidOperationException($"Failed to deserialize {filePath}");

        state ??= await StateStore.OpenAsync(StateStore.DefaultPathFor(filePath), ct);

        var tm = new TaskManager(filePath, data, state);

        var migrated = await TryMigrateLegacyDoneAsync(json, state, ct);
        if (migrated)
        {
            // ExtensionData에 흡수된 legacy `done` 키를 제거한 뒤 재저장.
            StripLegacyDoneFromExtensionData(data);
            await tm.SaveAsync(ct);
        }

        return tm;
    }

    /// <summary>
    /// raw JSON에서 legacy `done` 키를 찾아 StateStore로 이관한다.
    /// 발견되면 true 반환 (호출자가 tasks.json 재저장).
    /// </summary>
    private static async Task<bool> TryMigrateLegacyDoneAsync(string rawJson, StateStore state, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(rawJson);
        if (!doc.RootElement.TryGetProperty("tasks", out var tasksEl)
            || tasksEl.ValueKind != JsonValueKind.Array)
            return false;

        var anyLegacy = false;
        foreach (var taskEl in tasksEl.EnumerateArray())
        {
            if (taskEl.ValueKind != JsonValueKind.Object) continue;

            string? taskId = null;
            if (taskEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                taskId = idEl.GetString();

            if (string.IsNullOrEmpty(taskId)) continue;

            if (taskEl.TryGetProperty("done", out var doneEl)
                && (doneEl.ValueKind == JsonValueKind.True || doneEl.ValueKind == JsonValueKind.False))
            {
                anyLegacy = true;
                if (doneEl.ValueKind == JsonValueKind.True)
                    state.SetDoneInMemory(taskId, true);
            }

            if (taskEl.TryGetProperty("subtasks", out var subsEl)
                && subsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var subEl in subsEl.EnumerateArray())
                {
                    if (subEl.ValueKind != JsonValueKind.Object) continue;
                    string? subId = null;
                    if (subEl.TryGetProperty("id", out var subIdEl) && subIdEl.ValueKind == JsonValueKind.String)
                        subId = subIdEl.GetString();
                    if (string.IsNullOrEmpty(subId)) continue;

                    if (subEl.TryGetProperty("done", out var subDoneEl)
                        && (subDoneEl.ValueKind == JsonValueKind.True || subDoneEl.ValueKind == JsonValueKind.False))
                    {
                        anyLegacy = true;
                        if (subDoneEl.ValueKind == JsonValueKind.True)
                            state.SetSubtaskDoneInMemory(taskId, subId, true);
                    }
                }
            }
        }

        if (anyLegacy)
            await state.SaveAsync(ct);

        return anyLegacy;
    }

    /// <summary>
    /// `done` 키는 POCO에 더 이상 없으므로 ExtensionData(Dictionary&lt;string,JsonElement&gt;)에 들어간다.
    /// 재저장 전에 명시적으로 걷어내야 round-trip으로 살아남지 않는다.
    /// </summary>
    private static void StripLegacyDoneFromExtensionData(TasksFile data)
    {
        foreach (var task in data.Tasks)
        {
            task.ExtensionData?.Remove("done");
            if (task.Subtasks is null) continue;
            foreach (var sub in task.Subtasks)
                sub.ExtensionData?.Remove("done");
        }
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(_filePath, ct);
        _data = JsonSerializer.Deserialize<TasksFile>(json, JsonOptions)
                ?? throw new InvalidOperationException($"Failed to deserialize {_filePath}");
        await _state.ReloadAsync(ct);
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        var tmpFile = _filePath + $".tmp.{Guid.NewGuid():N}";
        try
        {
            var json = JsonSerializer.Serialize(_data, JsonOptions);

            // Validate before writing
            var verify = JsonSerializer.Deserialize<TasksFile>(json, JsonOptions);
            if (verify?.Tasks == null)
                throw new InvalidOperationException("Validation failed: tasks array missing");

            await File.WriteAllTextAsync(tmpFile, json, ct);
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

    public bool IsDone(string taskId) => _state.IsDone(taskId);

    public bool IsSubtaskDone(string taskId, string subtaskId)
        => _state.IsSubtaskDone(taskId, subtaskId);

    public List<TaskItem> GetPendingTasks()
        => _data.Tasks.Where(t => !_state.IsDone(t.Id)).ToList();

    public TaskItem? GetNextTask()
        => _data.Tasks.FirstOrDefault(t => !_state.IsDone(t.Id));

    public bool CheckDependencies(string taskId, out List<string> blockedBy)
    {
        blockedBy = [];
        var task = GetTask(taskId);
        if (task?.DependsOn is not { Count: > 0 })
            return true;

        foreach (var depId in task.DependsOn)
        {
            var dep = GetTask(depId);
            if (dep == null || !_state.IsDone(dep.Id))
                blockedBy.Add(depId);
        }

        return blockedBy.Count == 0;
    }

    public string? GetNextReadyTask()
    {
        foreach (var task in _data.Tasks.Where(t => !_state.IsDone(t.Id)))
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
            .Where(t => !_state.IsDone(t.Id) && CheckDependencies(t.Id, out _))
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

    /// <summary>
    /// 태스크를 done 처리한다. tasks.json은 변경되지 않고 state.json에만 기록된다.
    /// </summary>
    public Task MarkTaskDoneAsync(string taskId, CancellationToken ct = default)
    {
        if (GetTask(taskId) == null)
            throw new ArgumentException($"Task '{taskId}' not found");
        return _state.MarkDoneAsync(taskId, ct);
    }

    public Task MarkSubtaskDoneAsync(string taskId, string subtaskId, CancellationToken ct = default)
    {
        var task = GetTask(taskId)
                   ?? throw new ArgumentException($"Task '{taskId}' not found");
        if (task.Subtasks?.FirstOrDefault(s => s.Id == subtaskId) == null)
            throw new ArgumentException($"Subtask '{subtaskId}' not found");
        return _state.MarkSubtaskDoneAsync(taskId, subtaskId, ct);
    }

    /// <summary>
    /// 모든 진행 상태(state.json)를 초기화한다. tasks.json(spec)은 손대지 않는다.
    /// </summary>
    public Task ResetAllAsync(CancellationToken ct = default)
        => _state.ResetAllAsync(ct);

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
