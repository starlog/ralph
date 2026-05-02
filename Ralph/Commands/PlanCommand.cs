using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary>
/// <c>ralph --plan &lt;PRD.md&gt;</c> — PRD에서 tasks.json을 생성한다.
/// 기존 tasks.json은 timestamp 백업을 만든 뒤 덮어쓰기.
/// 옵션: <c>--llm-critique</c>면 생성 직후 LLM 비평 1회 추가.
/// </summary>
public sealed class PlanCommand : ICommand
{
    private readonly CommandContext _ctx;

    public PlanCommand(CommandContext ctx) => _ctx = ctx;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        if (_ctx.Args.Count < 2)
        {
            AnsiConsole.MarkupLine("[red]Error: PRD file required. Usage: ralph --plan <prd-file>[/]");
            return 1;
        }

        var prdFile = _ctx.Args[1];
        if (!File.Exists(prdFile))
        {
            AnsiConsole.MarkupLine($"[red]Error: File '{Markup.Escape(prdFile)}' not found.[/]");
            return 1;
        }

        // --run과 동일하게 세션 시작 시 배너 + 사용 모델을 출력한다. plan은 reasoning-heavy
        // 라 default가 opus이고 비용 차이가 크기 때문에 어떤 모델로 plan을 만드는지 가시화한다.
        DisplayHelpers.ShowBanner();
        var planModel = _ctx.ResolveModel("opus");
        var planModelSource = string.IsNullOrEmpty(_ctx.ModelArg) ? "default" : "--model";
        AnsiConsole.MarkupLine($"[cyan]Model:[/] {DisplayHelpers.FormatModel(planModel)} [dim]({planModelSource})[/]");
        AnsiConsole.MarkupLine($"[cyan]Input:[/]  {Markup.Escape(prdFile)}");
        AnsiConsole.MarkupLine($"[cyan]Output:[/] {Markup.Escape(_ctx.TasksFile)}");

        // 기존 tasks.json 백업
        if (File.Exists(_ctx.TasksFile))
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backupPath = $"{_ctx.TasksFile}.backup.{timestamp}";
            File.Copy(_ctx.TasksFile, backupPath);
            AnsiConsole.MarkupLine($"[yellow]기존 tasks.json을 백업했습니다: {Markup.Escape(backupPath)}[/]");
        }

        var schemaContent = SchemaLoader.Load();
        // tasks.json이 아직 없을 수 있으므로 workflow 적용 없이 cli/env/default만.
        var claude = _ctx.NewClaudeService(tm: null);
        var git = new GitService();
        using var logger = new RalphLogger();
        logger.Info($"Model: {planModel} ({planModelSource})");

        if (!await git.IsRepoInitializedAsync(ct))
            await git.InitAsync(logger, ct);

        // --rollback 지원: --plan 직전 상태(pre-plan)를 스냅샷으로 저장.
        // 이전 post-plan은 새 plan과 함께 stale이 되므로 CaptureBeforePlanAsync 안에서 정리한다.
        // 이 시점에선 worktree 지원을 위한 초기 커밋이 아직 없을 수 있으므로 먼저 보장한다 (HEAD 필요).
        await git.EnsureInitialCommitAsync(logger, ct);
        var rollback = new RollbackService();
        try
        {
            await rollback.CaptureBeforePlanAsync(git, _ctx.TasksFile, prdFile, ct);
        }
        catch (Exception ex)
        {
            logger.Warn($"pre-plan rollback snapshot 저장 실패 (계속 진행): {ex.Message}");
        }

        // 기존 tasks.json이 있으면 거기서 workflow.categories를 읽어 plan generator에 전달.
        IReadOnlyList<string>? configuredCategories = null;
        if (File.Exists(_ctx.TasksFile))
        {
            try
            {
                var existingTm = await TaskManager.LoadAsync(_ctx.TasksFile);
                var cats = existingTm.Data.Workflow?.Categories;
                if (cats is { Count: > 0 })
                    configuredCategories = cats.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            }
            catch (Exception ex)
            {
                logger.Warn(
                    $"기존 tasks.json 로드 실패 — default categories 사용: {ex.Message}");
            }
        }

        var generator = new PlanGenerator();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await generator.GenerateAsync(
            prdFile, schemaContent, _ctx.TasksFile, claude, planModel, logger,
            categories: configuredCategories, ct: ct);

        if (result != 0)
        {
            sw.Stop();
            return result;
        }

        // 검증 + 정정 루프: PlanValidator가 errors를 보고하면 현재 invalid tasks.json과 errors를
        // 다시 Claude에 보내 정정시킨다. warnings만 있는 경우는 통과로 처리.
        const int maxCorrectionAttempts = 2;
        PlanValidationReport report;
        var correctionAttempt = 0;
        while (true)
        {
            TaskManager validateTm;
            try
            {
                validateTm = await TaskManager.LoadAsync(_ctx.TasksFile);
            }
            catch (Exception ex)
            {
                sw.Stop();
                AnsiConsole.MarkupLine($"[red]✗ 생성된 tasks.json을 읽을 수 없습니다: {Markup.Escape(ex.Message)}[/]");
                return 1;
            }

            report = PlanValidator.Validate(validateTm);
            if (!report.HasErrors) break;

            if (correctionAttempt >= maxCorrectionAttempts)
            {
                sw.Stop();
                AnsiConsole.MarkupLine(
                    $"\n[red]✗ Plan 검증 실패 — {maxCorrectionAttempts}회 정정 시도 후에도 errors가 남았습니다:[/]");
                PlanValidator.PrintReport(report);
                AnsiConsole.MarkupLine(
                    "[yellow]'ralph --validate'로 자세히 확인하거나 PRD를 다듬어 다시 시도하세요.[/]");
                return 1;
            }

            correctionAttempt++;
            AnsiConsole.MarkupLine(
                $"\n[yellow]⚠ Plan 검증 실패 ({report.Errors.Count}개 error). " +
                $"AI 정정으로 재생성합니다 (시도 {correctionAttempt}/{maxCorrectionAttempts}):[/]");
            foreach (var e in report.Errors)
                AnsiConsole.MarkupLine($"  [red]•[/] {Markup.Escape(e)}");
            AnsiConsole.WriteLine();

            string currentInvalidJson;
            try
            {
                currentInvalidJson = await File.ReadAllTextAsync(_ctx.TasksFile, ct);
            }
            catch (Exception ex)
            {
                sw.Stop();
                AnsiConsole.MarkupLine($"[red]✗ tasks.json 읽기 실패: {Markup.Escape(ex.Message)}[/]");
                return 1;
            }

            var correctionContext = PlanGenerator.BuildCorrectionPrompt(
                currentInvalidJson, report.Errors, correctionAttempt, maxCorrectionAttempts);

            var fixResult = await generator.GenerateAsync(
                prdFile, schemaContent, _ctx.TasksFile, claude, planModel, logger,
                categories: configuredCategories,
                correctionContext: correctionContext, ct: ct);

            if (fixResult != 0)
            {
                sw.Stop();
                AnsiConsole.MarkupLine(
                    "[red]✗ 정정 시도 중 Claude 실행이 실패했습니다.[/]");
                return fixResult;
            }
        }

        // Warning 정정 루프: errors가 없고 warnings가 있으면 Claude로 자동 개선 시도.
        // 실패해도 warnings는 non-blocking이므로 계속 진행한다.
        const int maxWarningCorrectionAttempts = 2;
        var warningCorrectionAttempt = 0;
        while (report.HasWarnings && warningCorrectionAttempt < maxWarningCorrectionAttempts)
        {
            warningCorrectionAttempt++;
            AnsiConsole.MarkupLine(
                $"\n[yellow]⚠ Plan 검증 경고 {report.Warnings.Count}개. " +
                $"AI 정정으로 개선합니다 (시도 {warningCorrectionAttempt}/{maxWarningCorrectionAttempts}):[/]");
            foreach (var w in report.Warnings)
                AnsiConsole.MarkupLine($"  [yellow]•[/] {Markup.Escape(w)}");
            AnsiConsole.WriteLine();

            string currentJson;
            try
            {
                currentJson = await File.ReadAllTextAsync(_ctx.TasksFile, ct);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ tasks.json 읽기 실패, warning 정정 생략: {Markup.Escape(ex.Message)}[/]");
                break;
            }

            var warningCorrectionContext = PlanGenerator.BuildWarningCorrectionPrompt(
                currentJson, report.Warnings, warningCorrectionAttempt, maxWarningCorrectionAttempts);

            var warnFixResult = await generator.GenerateAsync(
                prdFile, schemaContent, _ctx.TasksFile, claude, planModel, logger,
                categories: configuredCategories,
                correctionContext: warningCorrectionContext, ct: ct);

            if (warnFixResult != 0)
            {
                AnsiConsole.MarkupLine("[yellow]⚠ Warning 정정 시도 중 Claude 실행이 실패했습니다. 경고를 유지합니다.[/]");
                break;
            }

            TaskManager revalidateTm;
            try
            {
                revalidateTm = await TaskManager.LoadAsync(_ctx.TasksFile);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ 재검증 중 tasks.json 읽기 실패: {Markup.Escape(ex.Message)}[/]");
                break;
            }

            report = PlanValidator.Validate(revalidateTm);

            // warning 정정 중 새 error가 생기면 즉시 실패 처리
            if (report.HasErrors)
            {
                sw.Stop();
                AnsiConsole.MarkupLine("[red]✗ Warning 정정 중 새로운 검증 errors가 발생했습니다:[/]");
                PlanValidator.PrintReport(report);
                return 1;
            }
        }

        sw.Stop();

        AnsiConsole.MarkupLine($"\n[green]플랜 생성 완료[/] [dim]({sw.Elapsed.Minutes}분 {sw.Elapsed.Seconds}초)[/]");
        if (correctionAttempt > 0)
            AnsiConsole.MarkupLine($"[dim](AI 정정 {correctionAttempt}회 후 검증 통과)[/]");
        if (warningCorrectionAttempt > 0 && !report.HasWarnings)
            AnsiConsole.MarkupLine($"[dim](Warning AI 정정 {warningCorrectionAttempt}회 후 경고 해소)[/]");

        // --rollback 지원: plan 성공 직후 상태(post-plan) 스냅샷 저장.
        try
        {
            await rollback.CaptureAfterPlanAsync(git, _ctx.TasksFile, prdFile, ct);
        }
        catch (Exception ex)
        {
            logger.Warn($"post-plan rollback snapshot 저장 실패 (계속 진행): {ex.Message}");
        }

        if (report.HasWarnings)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ Plan 검증 경고 {report.Warnings.Count}개 (정정 후에도 남은 경고):[/]");
            foreach (var w in report.Warnings)
                AnsiConsole.MarkupLine($"  [yellow]•[/] {Markup.Escape(w)}");
            AnsiConsole.WriteLine();
        }
        else
        {
            AnsiConsole.MarkupLine("[green]✓ Plan 검증 통과 (errors: 0, warnings: 0).[/]");
        }

        // PRD critique: 생성된 plan에 대한 정성 권고
        try
        {
            var critiqueTm = await TaskManager.LoadAsync(_ctx.TasksFile);
            var suggestions = PrdCritic.Analyze(critiqueTm);
            PrdCritic.PrintReport(suggestions);

            if (_ctx.LlmCritique)
            {
                await RunLlmCritiqueAsync(prdFile, critiqueTm, planModel, logger, ct);
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"PRD critique skipped: {ex.Message}");
        }

        return 0;
    }

    private async Task RunLlmCritiqueAsync(
        string prdFile, TaskManager tm, string model, RalphLogger logger, CancellationToken ct)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[blue]LLM Critique[/]").RuleStyle("blue"));
        AnsiConsole.MarkupLine("[dim]LLM에 PRD + 생성된 plan 요약을 보내 정성 비평을 요청합니다...[/]");

        string prdContent;
        try
        {
            prdContent = await File.ReadAllTextAsync(prdFile, ct);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ PRD 읽기 실패, LLM critique 생략: {Markup.Escape(ex.Message)}[/]");
            return;
        }

        var critiqueRunner = _ctx.NewClaudeService(tm);
        var cost = new CostTracker();
        var tasksFileBase = Path.GetFileNameWithoutExtension(tm.FilePath);
        if (string.IsNullOrEmpty(tasksFileBase)) tasksFileBase = "tasks";
        var costTaskId = $"critique:{tasksFileBase}";

        ClaudeResult? result = null;
        try
        {
            var prompt = LlmCritic.BuildPrompt(prdContent, tm);
            result = await critiqueRunner.RunStreamAsync(
                prompt, model: model, logger: logger, ct: ct, allowedTools: "");

            var output = result?.Output?.Trim();
            if (result?.Success == true && !string.IsNullOrWhiteSpace(output))
            {
                AnsiConsole.WriteLine();
                AnsiConsole.WriteLine(output);
                AnsiConsole.Write(new Rule().RuleStyle("blue"));
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ LLM critique 응답이 비어있거나 실패했습니다 (exit={result?.ExitCode ?? -1}).[/]");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ LLM critique 실패: {Markup.Escape(ex.Message)}[/]");
            logger.Warn($"LLM critique error: {ex.Message}");
        }
        finally
        {
            await cost.RecordAsync(costTaskId, model, result, CancellationToken.None);
        }
    }
}
