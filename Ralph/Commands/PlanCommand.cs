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

        if (!await git.IsRepoInitializedAsync(ct))
            await git.InitAsync(logger, ct);

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
            catch { /* best-effort: 깨진 기존 파일은 무시하고 default로 */ }
        }

        var generator = new PlanGenerator();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await generator.GenerateAsync(
            prdFile, schemaContent, _ctx.TasksFile, claude, _ctx.ResolveModel("opus"), logger,
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
                prdFile, schemaContent, _ctx.TasksFile, claude, _ctx.ResolveModel("opus"), logger,
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

        sw.Stop();

        AnsiConsole.MarkupLine($"\n[green]플랜 생성 완료[/] [dim]({sw.Elapsed.Minutes}분 {sw.Elapsed.Seconds}초)[/]");
        if (correctionAttempt > 0)
            AnsiConsole.MarkupLine($"[dim](AI 정정 {correctionAttempt}회 후 검증 통과)[/]");

        if (report.HasWarnings)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ Plan 검증 경고 {report.Warnings.Count}개:[/]");
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
                await RunLlmCritiqueAsync(prdFile, critiqueTm, _ctx.ResolveModel("opus"), logger, ct);
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
