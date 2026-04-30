using Ralph.Models;
using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary>
/// <c>ralph --rollback</c> — ralph 실행 직전 상태로 되돌린다 (파괴적, 사용자 확인 필요).
///
/// 동작:
///   - 현재 tasks.json에 done=true 작업이 있으면 ("after --run" 상태)
///       → post-plan 스냅샷으로 복원 (--plan 직후 상태로 되돌림)
///   - tasks.json은 있지만 done=true가 없으면 ("after --plan" 상태)
///       → pre-plan 스냅샷으로 복원 (ralph 실행 전 상태로 되돌림)
///   - 스냅샷이 없거나 tasks.json이 없으면 → 되돌릴 게 없음
///
/// 항상 confirmation 프롬프트로 사용자에게 묻는다 (--force 시 우회).
/// 비대화형 환경에서 --force 없이 호출하면 에러로 종료.
/// </summary>
public sealed class RollbackCommand : ICommand
{
    private readonly CommandContext _ctx;

    public RollbackCommand(CommandContext ctx) => _ctx = ctx;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        DisplayHelpers.ShowBanner();
        AnsiConsole.MarkupLine("[cyan]Mode:[/] rollback [dim](destructive — 사용자 확인 필요)[/]");

        var git = new GitService();
        if (!await git.IsRepoInitializedAsync(ct))
        {
            AnsiConsole.MarkupLine("[red]Error: Git 저장소가 아닙니다. rollback은 git 위에서만 동작합니다.[/]");
            return 1;
        }

        var rollback = new RollbackService();
        var prePlan = await rollback.LoadPrePlanAsync(ct);
        var postPlan = await rollback.LoadPostPlanAsync(ct);
        var tasksFile = _ctx.TasksFile;
        var tasksFileExists = File.Exists(tasksFile);

        // 현재 상태 판정 + rollback 대상 결정.
        var hasAnyDone = false;
        if (tasksFileExists)
        {
            try
            {
                var tm = await TaskManager.LoadAsync(tasksFile);
                hasAnyDone = tm.Data.Tasks.Any(t => t.Done);
            }
            catch
            {
                // 깨진 tasks.json — done 여부 판단 불가. pre-plan 복원으로 fallback.
            }
        }

        RollbackSnapshot? target;
        string currentStateLabel;
        string targetStateLabel;
        bool clearPostPlanAfter = false;
        bool clearPrePlanAfter = false;

        if (hasAnyDone)
        {
            currentStateLabel = "after --run (한 개 이상의 task가 done=true)";
            if (postPlan != null)
            {
                target = postPlan;
                targetStateLabel = "after --plan (모든 task pending, 코드 변경 없음)";
                clearPostPlanAfter = false; // pre-plan은 남아있으니 한 번 더 rollback 가능
            }
            else if (prePlan != null)
            {
                target = prePlan;
                targetStateLabel = "before ralph (post-plan 스냅샷이 없어 pre-plan으로 직접 복원)";
                clearPostPlanAfter = true;
                clearPrePlanAfter = true;
            }
            else
            {
                AnsiConsole.MarkupLine(
                    "[yellow]rollback 가능한 스냅샷이 없습니다 (.ralph-logs/rollback/ 없음).[/]");
                AnsiConsole.MarkupLine(
                    "[dim]스냅샷은 'ralph --plan'이 실행될 때 자동으로 생성됩니다.[/]");
                return 1;
            }
        }
        else if (tasksFileExists)
        {
            currentStateLabel = "after --plan (tasks.json 존재, done 작업 없음)";
            if (prePlan != null)
            {
                target = prePlan;
                targetStateLabel = "before ralph (tasks.json 제거 또는 이전 내용 복원)";
                clearPostPlanAfter = true;
                clearPrePlanAfter = true;
            }
            else
            {
                AnsiConsole.MarkupLine(
                    "[yellow]rollback 가능한 pre-plan 스냅샷이 없습니다.[/]");
                return 1;
            }
        }
        else
        {
            // tasks.json 자체가 없음 — 이미 "before ralph" 상태와 동등.
            AnsiConsole.MarkupLine(
                $"[yellow]tasks.json이 없습니다 ({Markup.Escape(tasksFile)}). rollback할 상태가 없습니다.[/]");
            return 0;
        }

        // 작업 요약 출력.
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]현재 상태:[/]   {Markup.Escape(currentStateLabel)}");
        AnsiConsole.MarkupLine($"[cyan]복원 대상:[/]   {Markup.Escape(targetStateLabel)}");
        AnsiConsole.MarkupLine($"[cyan]브랜치:[/]      {Markup.Escape(target.Branch)}");
        AnsiConsole.MarkupLine($"[cyan]대상 commit:[/] {Markup.Escape(ShortSha(target.GitHead))}");
        AnsiConsole.MarkupLine(
            $"[cyan]tasks.json:[/]  {(target.HadTasksJson ? "스냅샷 내용으로 덮어쓰기" : "삭제")}");
        AnsiConsole.MarkupLine($"[cyan]스냅샷 시각:[/] {Markup.Escape(target.Timestamp)}");
        AnsiConsole.WriteLine();

        // 파괴적 동작 — 변경 사항이 있으면 강하게 경고.
        var (statusExit, statusOut) = await git.RunAsync(["status", "--porcelain"], ct: ct);
        if (statusExit == 0 && !string.IsNullOrWhiteSpace(statusOut))
        {
            AnsiConsole.MarkupLine(
                "[red]⚠ 작업 디렉터리에 커밋되지 않은 변경 사항이 있습니다 — git reset --hard로 전부 사라집니다:[/]");
            foreach (var line in statusOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(10))
                AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(line.TrimEnd())}[/]");
            var more = statusOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 10;
            if (more > 0) AnsiConsole.MarkupLine($"  [dim]... 외 {more}개[/]");
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine(
            "[red]⚠ 이 동작은 파괴적입니다:[/]");
        AnsiConsole.MarkupLine(
            $"  • [yellow]git reset --hard {Markup.Escape(ShortSha(target.GitHead))}[/] (현재 브랜치의 이후 commit이 사라집니다)");
        if (target.HadTasksJson)
            AnsiConsole.MarkupLine(
                "  • tasks.json이 스냅샷 내용으로 덮어써집니다 (현재 내용 손실)");
        else
            AnsiConsole.MarkupLine(
                "  • tasks.json이 [red]삭제[/]됩니다");
        AnsiConsole.WriteLine();

        // 확인.
        if (!_ctx.ForceFlag)
        {
            var nonInteractive = Console.IsInputRedirected;
            if (nonInteractive)
            {
                AnsiConsole.MarkupLine(
                    "[red]비대화형 환경에서는 --force 없이 rollback할 수 없습니다.[/]");
                AnsiConsole.MarkupLine("  예: [cyan]ralph --rollback --force[/]");
                return 1;
            }
            var proceed = AnsiConsole.Confirm("[yellow]계속 진행하시겠습니까?[/]", defaultValue: false);
            if (!proceed)
            {
                AnsiConsole.MarkupLine("[dim]사용자 취소.[/]");
                return 1;
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]--force 지정됨 — 확인 없이 진행합니다.[/]");
        }

        // 실행.
        var (ok, msg) = await rollback.RestoreAsync(target, git, tasksFile, ct);
        if (!ok)
        {
            AnsiConsole.MarkupLine($"[red]✗ rollback 실패: {Markup.Escape(msg)}[/]");
            return 1;
        }

        // 사용한 스냅샷 정리.
        if (clearPostPlanAfter) rollback.ClearPostPlan();
        if (clearPrePlanAfter) rollback.ClearPrePlan();

        AnsiConsole.MarkupLine($"[green]✓ rollback 완료 — {Markup.Escape(targetStateLabel)}[/]");
        return 0;
    }

    private static string ShortSha(string sha)
        => string.IsNullOrEmpty(sha) ? "(unknown)" : (sha.Length > 8 ? sha[..8] : sha);
}
