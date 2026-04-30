using Ralph.Models;
using Spectre.Console;

namespace Ralph.Services;

/// <summary>
/// 단일 태스크를 worktree 안에서 실행하는 책임.
/// 진행 단계: log writer 오픈 → prompt+verification retry 루프 → tasks.json 가드 →
/// pre-commit scope guard → declared 파일만 staging해서 worktree 내 commit.
///
/// 이전에는 ParallelExecutor의 ~250 라인 메서드 4개에 분산되어 있던 일을 모았다.
/// 호출자(BatchOrchestrator)는 결과(success/fail)만 본다.
/// </summary>
internal sealed class WorktreeTaskRunner
{
    private readonly TaskManager _taskManager;
    private readonly GitService _git;
    private readonly RalphLogger _logger;
    private readonly VerificationLoop _verificationLoop;
    private readonly string _tasksFile;
    private readonly string? _modelOverride;
    private readonly bool _strictFiles;
    private readonly int _verifyRetries;

    public WorktreeTaskRunner(
        TaskManager taskManager, GitService git, RalphLogger logger,
        VerificationLoop verificationLoop,
        string tasksFile, string? modelOverride, bool strictFiles, int verifyRetries)
    {
        _taskManager = taskManager;
        _git = git;
        _logger = logger;
        _verificationLoop = verificationLoop;
        _tasksFile = tasksFile;
        _modelOverride = modelOverride;
        _strictFiles = strictFiles;
        _verifyRetries = verifyRetries;
    }

    /// <summary>
    /// worktree 안에서 태스크를 실행하며 출력을 로그 파일에 기록한다.
    /// 성공 시 worktree 내 commit까지 완료. caller(MergeOrchestrator)가 머지를 담당.
    /// </summary>
    public async Task<bool> RunAsync(
        string taskId, string worktreePath, IReadOnlyList<TaskItem> siblings,
        TaskProgressTracker tracker, CancellationToken ct)
    {
        var task = _taskManager.GetTask(taskId)!;
        _logger.TaskStart(taskId, task.Title);
        tracker.UpdateStatus(taskId, TaskProgressStatus.Running);

        const string logDir = RalphPaths.LogDir;
        var logFile = Path.GetFullPath(Path.Combine(logDir, $"{taskId}.log"));

        try
        {
            await using var logWriter = new StreamWriter(logFile, append: false) { AutoFlush = true };
            await logWriter.WriteLineAsync($"=== Task: {taskId} - {task.Title} ===");
            var (resolvedModel, modelSource) = ModelResolver.Resolve(_modelOverride, task);
            await logWriter.WriteLineAsync($"=== Model: {resolvedModel} ({modelSource}) ===");
            await logWriter.WriteLineAsync($"=== Started: {DateTime.Now} ===\n");
            tracker.SetModel(taskId, resolvedModel);
            _logger.Info($"[{taskId}] Model: {resolvedModel} ({modelSource})");

            if (!string.IsNullOrEmpty(task.Prompt))
            {
                var ok = await RunPromptWithVerificationAsync(
                    task, siblings, worktreePath, logWriter, tracker, resolvedModel, ct);
                if (!ok) return false;
            }

            // tasks.json worktree 보호: Claude가 실수로 또는 prompt를 무시하고
            // tasks.json을 수정했을 가능성을 방어. 머지 충돌의 가장 흔한 원인.
            await GuardTasksFileAsync(taskId, worktreePath, logWriter, ct);

            // F4-pre: 워크트리 staging 직전 scope 위반 검사.
            if (!await PreCommitScopeGuardAsync(task, worktreePath, logWriter, tracker, ct))
                return false;

            // worktree 안에서 커밋. declared 파일만 staging해서 격리 보장.
            if (_taskManager.CommitOnComplete)
            {
                var declared = DeclaredFiles.Build(task);
                await _git.CommitChangesAsync(
                    taskId, task.Title, _taskManager.CommitTemplate,
                    _logger, worktreePath, silent: true,
                    declaredFiles: declared, ct: ct);
            }

            tracker.UpdateStatus(taskId, TaskProgressStatus.Completed);
            await logWriter.WriteLineAsync($"\n=== Completed: {DateTime.Now} ===");
            _logger.TaskEnd(taskId, "completed-in-worktree");
            return true;
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C/취소를 task 실패로 변환하면 outer Task.WhenAll이 정상 완료처럼 보여
            // 후속 merge/cleanup 단계가 cancel을 무시한 채 계속 진행됨. 반드시 propagate.
            tracker.UpdateStatus(taskId, TaskProgressStatus.Failed);
            _logger.Warn($"Task {taskId} canceled in worktree");
            throw;
        }
        catch (Exception ex)
        {
            tracker.UpdateStatus(taskId, TaskProgressStatus.Failed);
            _logger.Error($"Task {taskId} failed in worktree: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Claude 실행 + (선택) 외부 verification 명령 실행. verification 실패 시 stdout/stderr를
    /// 다음 시도 prompt에 prepend해 self-fix 시도.
    /// </summary>
    private async Task<bool> RunPromptWithVerificationAsync(
        TaskItem task, IReadOnlyList<TaskItem> siblings, string worktreePath,
        TextWriter logWriter, TaskProgressTracker tracker, string model, CancellationToken ct)
    {
        var basePrompt = PromptBuilder.Build(task, _taskManager, _tasksFile, siblings);

        var callbacks = new VerificationCallbacks
        {
            OnClaudeFailure = result =>
            {
                tracker.UpdateStatus(task.Id, TaskProgressStatus.Failed);
                var exitInfo = result?.ExitCode.ToString() ?? "?";
                logWriter.WriteLine($"\n=== FAILED (exit code: {exitInfo}) ===");
                _logger.TaskEnd(task.Id, "failed");
            },
            OnVerificationFailFinal = (verify, attemptCount) =>
            {
                AnsiConsole.MarkupLine(
                    $"  [red]✗[/] {Markup.Escape(task.Id)} verification 실패 " +
                    $"(exit={verify.ExitCode}{(verify.TimedOut ? ", TIMEOUT" : "")}, {attemptCount}회 시도)");
                tracker.UpdateStatus(task.Id, TaskProgressStatus.Failed);
                _logger.Error(
                    $"[verification] {task.Id} failed exit={verify.ExitCode} timedOut={verify.TimedOut} " +
                    $"after {attemptCount} attempt(s)");
            },
            OnVerificationRetry = (_, attemptIndex, maxRetries) =>
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]⚠[/] {Markup.Escape(task.Id)} verification 실패 → Claude에게 수정 요청 ({attemptIndex}/{maxRetries} retry)");
                _logger.Warn(
                    $"[verification] {task.Id} failed (attempt {attemptIndex}); retrying with failure context");
            },
        };

        return await _verificationLoop.ExecuteAsync(
            task, basePrompt, _verifyRetries,
            claudeWorkingDirectory: worktreePath,
            verifierWorkingDirectory: worktreePath,
            output: logWriter,
            model: model,
            callbacks: callbacks,
            ct: ct);
    }

    /// <summary>
    /// Claude 실행 직후 staging 직전, worktree의 working-tree 변경 전체와 declared 집합을 비교한다.
    /// 새 파일/수정/삭제(staged·unstaged·untracked) 모두 보고 declared 외면 warn-only(또는 strict-files면 fail).
    /// commit 이후의 base...HEAD 검증과 보완 관계 — 이쪽은 staging 필터에 의해 사라지기 전 raw 변경을 본다.
    /// tasks.json은 별도 GuardTasksFileAsync가 정규화하므로 검사에서 제외.
    /// </summary>
    private async Task<bool> PreCommitScopeGuardAsync(
        TaskItem task, string worktreePath, TextWriter logWriter,
        TaskProgressTracker tracker, CancellationToken ct)
    {
        var (statusExit, statusOut) = await _git.RunAsync(
            ["status", "--porcelain"], worktreePath, ct);
        if (statusExit != 0)
        {
            await logWriter.WriteLineAsync($"\n=== [scope-guard] git status 실패 — skip ===");
            _logger.Warn($"[scope-guard] {task.Id}: git status 실패 — 검사 스킵");
            return true; // diff 실패가 머지를 막지 않게(F4와 동일 정책)
        }
        if (string.IsNullOrWhiteSpace(statusOut)) return true;

        var declared = DeclaredFiles.Build(task);
        var declaredSet = new HashSet<string>(
            declared.Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(p => p.Replace('\\', '/').Trim()),
            StringComparer.Ordinal);

        var tasksFileName = Path.GetFileName(_tasksFile);
        var changed = new List<string>();
        foreach (var line in statusOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // porcelain v1: "XY path" 또는 "XY orig -> new" (rename)
            if (line.Length < 4) continue;
            var rest = line[3..];
            string path;
            var arrowIdx = rest.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrowIdx >= 0) path = rest[(arrowIdx + 4)..].Trim();
            else path = rest.Trim();

            // 따옴표 제거 (renamed/space-containing 경로)
            if (path.Length >= 2 && path[0] == '"' && path[^1] == '"') path = path[1..^1];
            path = path.Replace('\\', '/').Trim();
            if (path.Length == 0) continue;
            if (string.Equals(path, tasksFileName, StringComparison.Ordinal)) continue;
            changed.Add(path);
        }

        var undeclared = changed
            .Where(p => !declaredSet.Contains(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (undeclared.Count == 0) return true;

        var preview = string.Join(", ", undeclared.Take(5));
        var more = undeclared.Count > 5 ? $" (외 {undeclared.Count - 5}건)" : "";

        if (_strictFiles)
        {
            await logWriter.WriteLineAsync(
                $"\n=== [scope-guard] STRICT FAIL: undeclared {undeclared.Count}건 — {preview}{more} ===");
            tracker.UpdateStatus(task.Id, TaskProgressStatus.Failed);
            _logger.Error(
                $"[scope-guard][strict] {task.Id} undeclared {undeclared.Count}건: {string.Join(", ", undeclared)}");
            return false;
        }

        await logWriter.WriteLineAsync(
            $"\n=== [scope-guard] WARN: undeclared {undeclared.Count}건 (warn-only) — {preview}{more} ===");
        _logger.Warn(
            $"[scope-guard] {task.Id} undeclared {undeclared.Count}건 (warn-only): {string.Join(", ", undeclared)}");
        return true;
    }

    /// <summary>
    /// worktree에서 tasks.json이 수정되었으면 강제로 되돌린다.
    /// Claude가 prompt 지시를 무시하거나 보조 작업으로 tasks.json을 건드린 경우의 안전망.
    /// 머지 단계에서 tasks.json 충돌(가장 흔한 충돌 케이스)을 사전 차단한다.
    /// </summary>
    private async Task GuardTasksFileAsync(
        string taskId, string worktreePath, TextWriter? logWriter, CancellationToken ct)
    {
        var tasksFileName = Path.GetFileName(_tasksFile);

        var (statusExit, statusOut) = await _git.RunAsync(
            ["status", "--porcelain", "--", tasksFileName], worktreePath, ct);

        if (statusExit != 0 || string.IsNullOrWhiteSpace(statusOut))
            return;

        var changeCode = statusOut.Length >= 2 ? statusOut[..2] : "";
        var x = changeCode.Length > 0 ? changeCode[0] : ' ';
        var y = changeCode.Length > 1 ? changeCode[1] : ' ';
        var msg = $"⚠️  worktree '{taskId}'에서 {tasksFileName}이 수정되었습니다 (status: '{changeCode.Trim()}'). 강제 되돌립니다.";
        _logger.Warn(msg);
        logWriter?.WriteLine($"\n=== {msg} ===");

        // staged 변경이 있으면 unstage
        await _git.RunAsync(["reset", "HEAD", "--", tasksFileName], worktreePath, ct);

        if (x == 'A' || x == '?')
        {
            // 새로 추가된 파일이면 HEAD에 없으므로 작업트리에서 제거
            var fullPath = Path.Combine(worktreePath, tasksFileName);
            try { if (File.Exists(fullPath)) File.Delete(fullPath); }
            catch (Exception ex) { _logger.Warn($"Failed to delete {fullPath}: {ex.Message}"); }
        }
        else if (x == 'M' || x == 'D' || x == 'R' || y == 'M' || y == 'D')
        {
            await _git.RunAsync(["checkout", "HEAD", "--", tasksFileName], worktreePath, ct);
        }
        else
        {
            _logger.Warn($"[GuardTasksFile] {taskId}: 알 수 없는 status '{changeCode}' — 무시");
        }
    }
}

/// <summary>
/// 태스크의 modifiedFiles ∪ outputFiles를 normalized 집합으로 만드는 공용 헬퍼.
/// WorktreeTaskRunner와 MergeOrchestrator가 공유.
/// </summary>
internal static class DeclaredFiles
{
    public static IReadOnlyCollection<string> Build(TaskItem task)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (task.ModifiedFiles is { Count: > 0 })
        {
            foreach (var p in task.ModifiedFiles)
                if (!string.IsNullOrWhiteSpace(p)) set.Add(p);
        }
        if (task.OutputFiles is { Count: > 0 })
        {
            foreach (var p in task.OutputFiles)
                if (!string.IsNullOrWhiteSpace(p)) set.Add(p);
        }
        return set;
    }
}
