using System.Text.Encodings.Web;
using System.Text.Json;
using Ralph.Models;

namespace Ralph.Services;

/// <summary>
/// Mutable per-task progress (done 비트) 저장소.
/// tasks.json(immutable spec)과 분리되어 `.ralph-logs/state.json`에 저장된다.
/// Orchestrator process 단독 writer. 모든 mutator는 SemaphoreSlim으로 직렬화한다.
/// 저장은 atomic (tmp+rename).
/// </summary>
public class StateStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = RalphJsonContext.Default,
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private StateFile _data;

    public string FilePath => _filePath;
    public StateFile Data => _data;

    private StateStore(string filePath, StateFile data)
    {
        _filePath = filePath;
        _data = data;
    }

    /// <summary>
    /// 주어진 tasks.json 경로에 대응하는 기본 state.json 경로를 계산한다.
    /// `<dir of tasksFile>/.ralph-logs/state.json`.
    /// </summary>
    public static string DefaultPathFor(string tasksFilePath)
    {
        var fullTasks = Path.GetFullPath(tasksFilePath);
        var dir = Path.GetDirectoryName(fullTasks) ?? Directory.GetCurrentDirectory();
        return Path.Combine(dir, ".ralph-logs", "state.json");
    }

    public static async Task<StateStore> OpenAsync(string filePath, CancellationToken ct = default)
    {
        StateFile data;
        if (File.Exists(filePath))
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            data = JsonSerializer.Deserialize<StateFile>(json, JsonOptions) ?? new StateFile();
            data.Tasks ??= new Dictionary<string, TaskState>();
        }
        else
        {
            data = new StateFile();
        }
        return new StateStore(filePath, data);
    }

    public bool IsDone(string taskId)
        => _data.Tasks.TryGetValue(taskId, out var ts) && ts.Done;

    public bool IsSubtaskDone(string taskId, string subtaskId)
        => _data.Tasks.TryGetValue(taskId, out var ts)
           && ts.Subtasks is { } subs
           && subs.TryGetValue(subtaskId, out var done) && done;

    public async Task MarkDoneAsync(string taskId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            EnsureTaskState(taskId).Done = true;
            await SaveInternalAsync(ct);
        }
        finally { _lock.Release(); }
    }

    public async Task MarkSubtaskDoneAsync(string taskId, string subtaskId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var ts = EnsureTaskState(taskId);
            ts.Subtasks ??= new Dictionary<string, bool>();
            ts.Subtasks[subtaskId] = true;
            await SaveInternalAsync(ct);
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// 모든 진행 상태를 초기화한다. tasks.json(spec)은 손대지 않는다.
    /// </summary>
    public async Task ResetAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _data = new StateFile();
            await SaveInternalAsync(ct);
        }
        finally { _lock.Release(); }
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (File.Exists(_filePath))
            {
                var json = await File.ReadAllTextAsync(_filePath, ct);
                _data = JsonSerializer.Deserialize<StateFile>(json, JsonOptions) ?? new StateFile();
                _data.Tasks ??= new Dictionary<string, TaskState>();
            }
            else
            {
                _data = new StateFile();
            }
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Legacy 마이그레이션 전용: 락 없이 in-memory만 갱신한다.
    /// 마이그레이션 도중 외부 락이 걸려있을 때만 호출.
    /// </summary>
    internal void SetDoneInMemory(string taskId, bool done)
        => EnsureTaskState(taskId).Done = done;

    internal void SetSubtaskDoneInMemory(string taskId, string subtaskId, bool done)
    {
        var ts = EnsureTaskState(taskId);
        ts.Subtasks ??= new Dictionary<string, bool>();
        ts.Subtasks[subtaskId] = done;
    }

    /// <summary>외부에서 atomic save를 트리거 (마이그레이션 후 등).</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { await SaveInternalAsync(ct); }
        finally { _lock.Release(); }
    }

    private TaskState EnsureTaskState(string taskId)
    {
        if (!_data.Tasks.TryGetValue(taskId, out var ts))
        {
            ts = new TaskState();
            _data.Tasks[taskId] = ts;
        }
        return ts;
    }

    private async Task SaveInternalAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = _filePath + $".tmp.{Guid.NewGuid():N}";
        try
        {
            var json = JsonSerializer.Serialize(_data, JsonOptions);
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
            }
            throw;
        }
    }
}
