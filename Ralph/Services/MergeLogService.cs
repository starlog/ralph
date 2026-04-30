using System.Text.Json;
using System.Text.Json.Serialization;
using Ralph.Models;

namespace Ralph.Services;

/// <summary>
/// .ralph-logs/merge-log.jsonl에 머지 트랜잭션을 idempotent하게 append하고 읽는 서비스.
/// CostTracker의 SemaphoreSlim(1,1) + RalphJsonContext source-gen 패턴을 그대로 따른다.
///
/// idempotency 키:
///   merge entry   → (taskId, mergedSha)
///   rollback entry → (taskId, rollbackRevertSha)
///
/// 잠금: SemaphoreSlim(1,1). append 실패는 warn 후 silent — merge-log 없음이
/// 머지 커밋/state를 바꾸지 않으므로 batch를 중단하지 않는다.
/// </summary>
public sealed class MergeLogService
{
    private readonly string _logFilePath;
    private readonly RalphLogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly HashSet<(string TaskId, string MergedSha)> _seenMerges = new();
    private readonly HashSet<(string TaskId, string RevertSha)> _seenRollbacks = new();
    private bool _loaded;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = RalphJsonContext.Default,
    };

    public MergeLogService(string repoRoot, RalphLogger logger)
    {
        _logFilePath = Path.Combine(repoRoot, RalphPaths.MergeLogRelative);
        _logger = logger;
    }

    /// <summary>
    /// merge entry를 idempotent하게 append한다. (taskId, mergedSha) 중복이면 silent skip.
    /// IO 실패 시 warn 후 계속 진행.
    /// </summary>
    public async Task AppendMergeAsync(MergeLogEntry entry, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            if (!_seenMerges.Add((entry.TaskId, entry.MergedSha))) return;
            await AppendLineAsync(entry, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// rollback entry를 idempotent하게 append한다. (taskId, rollbackRevertSha) 중복이면 silent skip.
    /// IO 실패 시 warn 후 계속 진행.
    /// </summary>
    public async Task AppendRollbackAsync(MergeLogEntry entry, CancellationToken ct)
    {
        var revertSha = entry.RollbackRevertSha ?? "";
        await _writeLock.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            if (!_seenRollbacks.Add((entry.TaskId, revertSha))) return;
            await AppendLineAsync(entry, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 모든 entry를 읽어 반환. 파일이 없거나 IO 실패 시 빈 리스트.
    /// reader는 잠금 없이 전체 파일을 읽고 끝 줄이 깨진 경우 skip.
    /// </summary>
    public async Task<IReadOnlyList<MergeLogEntry>> ReadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(_logFilePath)) return Array.Empty<MergeLogEntry>();
        try
        {
            var lines = await File.ReadAllLinesAsync(_logFilePath, ct);
            var result = new List<MergeLogEntry>(lines.Length);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize(line, RalphJsonContext.Default.MergeLogEntry);
                    if (entry is not null) result.Add(entry);
                }
                catch { /* 깨진 줄 skip — defensive parsing */ }
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.Warn($"[merge-log] ReadAllAsync 실패 (비치명): {ex.Message}");
            return Array.Empty<MergeLogEntry>();
        }
    }

    // 첫 번째 write 직전 기존 파일을 한 번 스캔해 dedup set을 hydrate한다.
    // 같은 프로세스 안에서만 유효 (다른 ralph 프로세스와의 dedup은 보장하지 않음).
    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        _loaded = true;

        if (!File.Exists(_logFilePath)) return;
        try
        {
            var lines = await File.ReadAllLinesAsync(_logFilePath, ct);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize(line, RalphJsonContext.Default.MergeLogEntry);
                    if (entry is null) continue;
                    if (string.IsNullOrEmpty(entry.Event) || entry.Event == "merge")
                        _seenMerges.Add((entry.TaskId, entry.MergedSha));
                    else if (entry.Event == "rollback")
                        _seenRollbacks.Add((entry.TaskId, entry.RollbackRevertSha ?? ""));
                }
                catch { /* skip */ }
            }
        }
        catch { /* 읽기 실패 → 빈 set으로 시작 */ }
    }

    private async Task AppendLineAsync(MergeLogEntry entry, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(_logFilePath)!;
            Directory.CreateDirectory(dir);
            var line = JsonSerializer.Serialize(entry, RalphJsonContext.Default.MergeLogEntry) + "\n";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await File.AppendAllTextAsync(_logFilePath, line, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[merge-log] append 실패 (비치명): {ex.Message}");
        }
    }
}
