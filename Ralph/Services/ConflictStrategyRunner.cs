using Spectre.Console;

namespace Ralph.Services;

/// <summary>
/// 머지 충돌 해결을 담당하는 컴포넌트.
/// strategyChain을 순차로 시도해 한 가지라도 성공하면 true, 모두 실패하면 false 반환.
/// chain 항목: <c>auto-theirs</c> / <c>auto-ours</c> / <c>claude</c> / <c>abort</c>.
/// chain[0]은 이미 1차 머지 명령에 -X 옵션으로 적용되어 시도된 상태이므로 첫 항목이
/// auto-* 인 경우 즉시 다음 fallback으로 진행한다.
/// </summary>
internal sealed class ConflictStrategyRunner
{
    private readonly IAgentRunner _claude;
    private readonly GitService _git;
    private readonly WorktreeService _worktree;
    private readonly RalphLogger _logger;
    private readonly CostTracker _cost;
    private readonly string? _model;

    public ConflictStrategyRunner(
        IAgentRunner claude, GitService git, WorktreeService worktree,
        RalphLogger logger, CostTracker cost, string? model)
    {
        _claude = claude;
        _git = git;
        _worktree = worktree;
        _logger = logger;
        _cost = cost;
        _model = model;
    }

    /// <summary>
    /// strategyChain을 순회하며 충돌 해결을 시도한다.
    /// abort 전략은 <paramref name="rerunSequential"/> 콜백으로 fallback한다 — 콜백이 null이면
    /// abort 단계에서 false 반환.
    /// </summary>
    public async Task<bool> ResolveAsync(
        string taskId, string baseBranch, MergeResult mergeResult,
        IReadOnlyList<string> chain,
        Func<string, CancellationToken, Task<int>>? rerunSequential,
        CancellationToken ct)
    {
        var currentMergeResult = mergeResult;

        for (var i = 0; i < chain.Count; i++)
        {
            var strategy = chain[i];
            var isFirst = i == 0;

            switch (strategy)
            {
                case "claude":
                    AnsiConsole.MarkupLine(
                        $"  [cyan]충돌 해결 시도: claude (전략 {i + 1}/{chain.Count})[/]");
                    if (await ResolveWithClaudeAsync(taskId, currentMergeResult, ct))
                        return true;
                    AnsiConsole.MarkupLine("  [yellow]claude 해결 실패 — 다음 전략 시도[/]");
                    _logger.Warn($"[merge:chain] {taskId} claude failed at step {i + 1}/{chain.Count}");
                    break;

                case "abort":
                    await _worktree.AbortMergeAsync(ct);
                    AnsiConsole.MarkupLine(
                        $"[yellow]전략 abort (전략 {i + 1}/{chain.Count}): " +
                        $"{Markup.Escape(taskId)}를 순차 모드로 재실행합니다...[/]");
                    _logger.Warn($"[merge:chain] {taskId} abort -> sequential rerun at step {i + 1}");
                    if (rerunSequential is null)
                    {
                        _logger.Error($"[merge:chain] {taskId} abort but no RerunSequential callback registered");
                        return false;
                    }
                    return await rerunSequential(taskId, ct) == 0;

                case "auto-theirs":
                case "auto-ours":
                    if (isFirst)
                    {
                        AnsiConsole.MarkupLine(
                            $"  [yellow]{strategy}로 풀 수 없는 충돌 (add/add, rename/delete 등). 다음 전략 시도[/]");
                        _logger.Warn($"[merge:chain] {taskId} {strategy} (-X) 첫 시도에서 미해결 충돌");
                    }
                    else
                    {
                        await _worktree.AbortMergeAsync(ct);
                        AnsiConsole.MarkupLine(
                            $"  [cyan]전략 {strategy}로 재머지 시도 (전략 {i + 1}/{chain.Count})[/]");
                        var retry = await _worktree.MergeWorktreeAsync(
                            taskId, baseBranch, strategy, _logger, ct);
                        if (retry.Success)
                        {
                            AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(taskId)} {strategy} 재머지 성공");
                            return true;
                        }
                        currentMergeResult = retry;
                        AnsiConsole.MarkupLine($"  [yellow]{strategy} 재머지 실패 — 다음 전략 시도[/]");
                        _logger.Warn($"[merge:chain] {taskId} {strategy} retry failed at step {i + 1}");
                    }
                    break;

                default:
                    AnsiConsole.MarkupLine(
                        $"  [yellow]알 수 없는 전략 무시: {Markup.Escape(strategy)}[/]");
                    _logger.Warn($"[merge:chain] {taskId} unknown strategy: {strategy}");
                    break;
            }
        }

        AnsiConsole.MarkupLine(
            $"  [red]✗[/] {Markup.Escape(taskId)} 모든 conflict 전략 실패 ({chain.Count}개 시도)");
        _logger.Error($"[merge:chain] {taskId} all {chain.Count} strategies exhausted");
        await _worktree.AbortMergeAsync(ct);
        return false;
    }

    /// <summary>
    /// Claude를 사용하여 merge 충돌을 해결한다. 성공 시 staging + commit까지 수행하고 true 반환.
    /// 어느 단계라도 실패하면 abort 후 false.
    /// </summary>
    private async Task<bool> ResolveWithClaudeAsync(
        string taskId, MergeResult mergeResult, CancellationToken ct)
    {
        if (mergeResult.ConflictFiles is not { Count: > 0 })
        {
            await _worktree.AbortMergeAsync(ct);
            return false;
        }

        var repoRoot = await _git.GetRepoRootAsync(ct: ct);

        var conflictList = string.Join("\n", mergeResult.ConflictFiles.Select(f => $"  - {f}"));
        var prompt = $"""
            다음 git merge 충돌을 해결해주세요.

            작업 디렉토리: {repoRoot}
            태스크: {taskId}
            충돌 파일 (repo 루트 기준 상대 경로):
            {conflictList}

            지시:
            1. `git status`로 현재 충돌 상태를 확인하세요.
            2. 위 각 파일을 열어 충돌 마커(<<<<<<< HEAD, =======, >>>>>>> branch)를 모두 제거하세요.
            3. 양쪽 변경사항을 모두 살리는 방향으로 통합하세요.
            4. 마커가 남아있는지 검증한 뒤 파일을 저장하세요. (마커가 남아있으면 빌드/실행이 깨집니다)

            staging과 commit은 ralph가 처리하므로 git add/commit은 실행하지 마세요.
            """;

        AnsiConsole.MarkupLine($"[cyan]Claude Code로 충돌 해결 중 ({mergeResult.ConflictFiles.Count}개 파일, repo: {Markup.Escape(repoRoot)})...[/]");

        // 머지 충돌 해결은 특정 task가 아니라 batch 결과에 대한 작업이므로 task.model을 쓰지 않고
        // 명시적 override 또는 기본값(sonnet)을 사용한다.
        var conflictModel = ModelResolver.ResolveForNonTask(_model);
        ClaudeResult? result = null;
        try
        {
            result = await _claude.RunWithRetryAsync(
                prompt, model: conflictModel, workingDirectory: repoRoot, logger: _logger, ct: ct);
        }
        finally
        {
            await _cost.RecordAsync($"conflict:{taskId}", conflictModel, result, CancellationToken.None);
        }
        if (result == null || !result.Success)
        {
            await _worktree.AbortMergeAsync(ct);
            return false;
        }

        // 해결된 파일에 충돌 마커가 남아있는지 1차 검증.
        foreach (var file in mergeResult.ConflictFiles)
        {
            var fullPath = Path.Combine(repoRoot, file);
            if (File.Exists(fullPath))
            {
                var content = await File.ReadAllTextAsync(fullPath, ct);
                if (content.Contains("<<<<<<<") || content.Contains(">>>>>>>"))
                {
                    AnsiConsole.MarkupLine($"[red]충돌 마커가 여전히 남아있음: {Markup.Escape(file)}[/]");
                    _logger.Error($"Conflict markers remain in {file} after Claude resolution");
                    await _worktree.AbortMergeAsync(ct);
                    return false;
                }
            }
        }

        // 해결된 파일 staging
        foreach (var file in mergeResult.ConflictFiles)
        {
            await _git.RunAsync(["add", "--", file], workingDirectory: repoRoot, ct: ct);
        }

        // P1-2: staged 영역 전체를 git diff --check --cached로 한 번 더 검증.
        var (checkExit, checkOut) = await _git.RunAsync(
            ["diff", "--check", "--cached"], workingDirectory: repoRoot, ct: ct);
        if (checkExit != 0)
        {
            AnsiConsole.MarkupLine($"[red]staged 영역에 충돌 마커/문제 감지:[/]");
            if (!string.IsNullOrWhiteSpace(checkOut))
                AnsiConsole.WriteLine(checkOut.Trim());
            _logger.Error($"git diff --check --cached failed for {taskId}: {checkOut.Trim()}");
            await _worktree.AbortMergeAsync(ct);
            return false;
        }

        var (exitCode, _) = await _git.RunAsync(
            ["commit", "--no-edit"], workingDirectory: repoRoot, ct: ct);

        if (exitCode == 0)
        {
            AnsiConsole.MarkupLine($"[green]충돌 해결 완료: {Markup.Escape(taskId)}[/]");
            _logger.Info($"Conflict resolved via Claude for {taskId}");
            return true;
        }

        await _worktree.AbortMergeAsync(ct);
        return false;
    }
}
