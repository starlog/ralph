using System.Text.Encodings.Web;
using System.Text.Json;
using Ralph.Models;

namespace Ralph.Services;

/// <summary>
/// fix2 #7: batch 단위 자동 롤백을 위한 in-memory 스냅샷.
/// 디스크에 직렬화하지 않는다 — 한 batch가 진행되는 동안만 유효하며 smoke 실패 핸들러가
/// 즉시 사용 후 폐기한다. pre-plan/post-plan 디스크 스냅샷과는 완전히 별개다.
/// </summary>
/// <param name="BaseBranch">batch가 머지된 base 브랜치 이름.</param>
/// <param name="BaseSha">batch 시작 직전(머지 0회 시점) base 브랜치의 HEAD SHA.</param>
/// <param name="CapturedAt">스냅샷 생성 UTC 시각 (진단/로그용).</param>
/// <param name="TaskIds">batch에 포함된 task ID들. 이후 머지 단계에서 일부가 rebase 충돌로
/// 빠질 수 있으므로 자동 revert 대상은 호출자가 별도로 mergedTasks를 전달한다.</param>
public sealed record BatchRollbackSnapshot(
    string BaseBranch,
    string BaseSha,
    DateTime CapturedAt,
    IReadOnlyList<string> TaskIds);

/// <summary>
/// --rollback이 의존하는 스냅샷 capture/restore 로직.
///
/// 두 개의 스냅샷을 .ralph-logs/rollback/ 아래 보관:
///   - pre-plan.json  : --plan 직전 상태 (rollback 대상 = "before ralph execution")
///   - post-plan.json : --plan 직후 상태 (rollback 대상 = "after --plan / before --run")
///
/// --plan은 두 스냅샷을 모두 갱신한다. --run은 스냅샷을 만지지 않는다.
/// --rollback은 현재 state.json(.ralph-logs/state.json) 상태로 어느 스냅샷을 적용할지 판단:
///   state.json에 done=true 있음 → post-plan으로 복원 (run 결과 되돌리기)
///   state.json에 done 없음        → pre-plan으로 복원 (plan 결과 되돌리기)
/// </summary>
public sealed class RollbackService
{
    public const string PrePlanPhase = "pre-plan";
    public const string PostPlanPhase = "post-plan";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = RalphJsonContext.Default,
    };

    private readonly string _rollbackDir;

    public RollbackService(string logDir = RalphPaths.LogDir)
    {
        _rollbackDir = Path.Combine(logDir, RalphPaths.RollbackDirName);
    }

    public string PrePlanPath => Path.Combine(_rollbackDir, RalphPaths.PrePlanSnapshotFileName);
    public string PostPlanPath => Path.Combine(_rollbackDir, RalphPaths.PostPlanSnapshotFileName);

    /// <summary>
    /// --plan 실행 직전에 호출. 현재 git HEAD + tasks.json + PRD 원본을 pre-plan 스냅샷으로 저장.
    /// 동시에 더 이상 유효하지 않은 post-plan 스냅샷은 제거한다 (새 plan은 새 post-plan을 만든다).
    /// </summary>
    public async Task CaptureBeforePlanAsync(
        GitService git, string tasksFile, string prdFile, CancellationToken ct = default)
    {
        var snapshot = await BuildSnapshotAsync(PrePlanPhase, git, tasksFile, prdFile, ct);
        await WriteSnapshotAsync(PrePlanPath, snapshot, ct);

        // pre-plan을 새로 잡았다면 이전 post-plan은 stale — 제거한다.
        if (File.Exists(PostPlanPath))
        {
            try { File.Delete(PostPlanPath); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// --plan 성공 직후에 호출. 현재 git HEAD + 새로 생성된 tasks.json + PRD 원본을 post-plan 스냅샷으로 저장.
    /// </summary>
    public async Task CaptureAfterPlanAsync(
        GitService git, string tasksFile, string prdFile, CancellationToken ct = default)
    {
        var snapshot = await BuildSnapshotAsync(PostPlanPhase, git, tasksFile, prdFile, ct);
        await WriteSnapshotAsync(PostPlanPath, snapshot, ct);
    }

    /// <summary>
    /// fix2 #7: batch 머지 시작 직전에 in-memory 스냅샷을 캡처한다. 디스크 I/O 없음.
    /// 호출자(MergeOrchestrator)는 smoke 실패 시 이 스냅샷을 자동 revert 핸들러에 전달한다.
    /// </summary>
    public static BatchRollbackSnapshot CaptureBatchSnapshot(
        string baseBranch, string baseSha, IReadOnlyList<string> taskIds)
        => new(baseBranch, baseSha, DateTime.UtcNow, taskIds);

    public async Task<RollbackSnapshot?> LoadPrePlanAsync(CancellationToken ct = default)
        => await TryLoadAsync(PrePlanPath, ct);

    public async Task<RollbackSnapshot?> LoadPostPlanAsync(CancellationToken ct = default)
        => await TryLoadAsync(PostPlanPath, ct);

    /// <summary>두 스냅샷 모두 삭제.</summary>
    public void ClearAll()
    {
        if (File.Exists(PrePlanPath))
            try { File.Delete(PrePlanPath); } catch { /* best-effort: 스냅샷 삭제 실패는 다음 plan/run에서 덮어씌워짐 */ }
        if (File.Exists(PostPlanPath))
            try { File.Delete(PostPlanPath); } catch { /* best-effort: 스냅샷 삭제 실패는 다음 plan/run에서 덮어씌워짐 */ }
    }

    public void ClearPrePlan()
    {
        if (File.Exists(PrePlanPath))
            try { File.Delete(PrePlanPath); } catch { /* best-effort: 스냅샷 삭제 실패는 다음 plan/run에서 덮어씌워짐 */ }
    }

    public void ClearPostPlan()
    {
        if (File.Exists(PostPlanPath))
            try { File.Delete(PostPlanPath); } catch { /* best-effort: 스냅샷 삭제 실패는 다음 plan/run에서 덮어씌워짐 */ }
    }

    /// <summary>
    /// 스냅샷대로 git/tasks.json/PRD를 복원한다.
    /// - git reset --hard {snapshot.GitHead}
    /// - snapshot.HadTasksJson==true: tasksFile에 atomic write
    /// - snapshot.HadTasksJson==false: tasksFile 삭제
    /// - snapshot.HadPrdFile==true: PrdFilePath에 atomic write (git reset에서 잃어버린 PRD를 복구)
    /// PRD는 plan 입력 파일이므로 항상 보존되어야 한다 (사라지면 사용자가 재작성해야 함).
    /// </summary>
    public async Task<(bool Ok, string Message)> RestoreAsync(
        RollbackSnapshot snapshot, GitService git, string tasksFile, CancellationToken ct = default)
    {
        // git reset --hard
        var (exit, output) = await git.RunAsync(["reset", "--hard", snapshot.GitHead], ct: ct);
        if (exit != 0)
        {
            return (false, $"git reset --hard 실패: {output.Trim()}");
        }

        // tasks.json 복원/삭제
        if (snapshot.HadTasksJson)
        {
            try
            {
                var tmpFile = tasksFile + $".tmp.{Guid.NewGuid():N}";
                await File.WriteAllTextAsync(tmpFile, snapshot.TasksJsonContent ?? "", ct);
                File.Move(tmpFile, tasksFile, overwrite: true);
            }
            catch (Exception ex)
            {
                return (false, $"tasks.json 복원 실패: {ex.Message}");
            }
        }
        else
        {
            if (File.Exists(tasksFile))
            {
                try { File.Delete(tasksFile); }
                catch (Exception ex) { return (false, $"tasks.json 삭제 실패: {ex.Message}"); }
            }
        }

        // PRD 복원: git reset이 PRD를 워킹트리에서 지웠을 수 있으므로(스냅샷 시점에 untracked였다가
        // --run 도중 git add -A로 commit된 케이스) snapshot 내용을 디스크에 다시 쓴다.
        // 이미 동일 내용이 있어도 atomic overwrite로 안전하다.
        if (snapshot.HadPrdFile && !string.IsNullOrEmpty(snapshot.PrdFilePath))
        {
            try
            {
                var prdPath = snapshot.PrdFilePath;
                var prdDir = Path.GetDirectoryName(prdPath);
                if (!string.IsNullOrEmpty(prdDir) && !Directory.Exists(prdDir))
                    Directory.CreateDirectory(prdDir);
                var tmpPrd = prdPath + $".tmp.{Guid.NewGuid():N}";
                await File.WriteAllTextAsync(tmpPrd, snapshot.PrdContent ?? "", ct);
                File.Move(tmpPrd, prdPath, overwrite: true);
            }
            catch (Exception ex)
            {
                return (false, $"PRD 복원 실패 ({snapshot.PrdFilePath}): {ex.Message}");
            }
        }

        return (true, "복원 완료");
    }

    private static async Task<RollbackSnapshot> BuildSnapshotAsync(
        string phase, GitService git, string tasksFile, string prdFile, CancellationToken ct)
    {
        var (headExit, headOut) = await git.RunAsync(["rev-parse", "HEAD"], ct: ct);
        var head = headExit == 0 ? headOut.Trim() : "";
        var branch = await git.GetCurrentBranchAsync(ct: ct);

        bool had = File.Exists(tasksFile);
        string? content = null;
        if (had)
        {
            try { content = await File.ReadAllTextAsync(tasksFile, ct); }
            catch { had = false; content = null; }
        }

        bool hadPrd = !string.IsNullOrEmpty(prdFile) && File.Exists(prdFile);
        string? prdContent = null;
        if (hadPrd)
        {
            try { prdContent = await File.ReadAllTextAsync(prdFile, ct); }
            catch { hadPrd = false; prdContent = null; }
        }

        return new RollbackSnapshot
        {
            Phase = phase,
            Timestamp = DateTime.UtcNow.ToString("o"),
            GitHead = head,
            Branch = branch,
            HadTasksJson = had,
            TasksJsonContent = content,
            TasksFilePath = tasksFile,
            HadPrdFile = hadPrd,
            PrdContent = prdContent,
            PrdFilePath = prdFile ?? "",
        };
    }

    private async Task WriteSnapshotAsync(string path, RollbackSnapshot snapshot, CancellationToken ct)
    {
        Directory.CreateDirectory(_rollbackDir);
        var tmp = path + $".tmp.{Guid.NewGuid():N}";
        try
        {
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmp))
                try { File.Delete(tmp); } catch { /* tmp 정리 실패는 의도적 무시: 원인 예외 보존이 우선 */ }
            throw;
        }
    }

    private static async Task<RollbackSnapshot?> TryLoadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<RollbackSnapshot>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
