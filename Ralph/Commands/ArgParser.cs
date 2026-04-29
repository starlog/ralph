using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary>
/// argv → <see cref="CommandContext"/> 변환. 환경변수 read도 여기서 모은다.
/// parse 실패 시 stderr에 사용자 메시지를 찍고 <c>null</c> 반환 → 호출자(Program)는 종료 코드 1.
///
/// Program.cs top-level에 흩어져 있던 ~120 lines의 flag/env 처리를 한 곳으로 모았다.
/// </summary>
public static class ArgParser
{
    /// <summary>positional 파일 인자를 허용하는 명령들.</summary>
    private static readonly HashSet<string> FilePositionalCommands = new()
    {
        "--run", "--dry-run", "--list", "-l", "--graph", "-g",
        "--prompts", "-p", "--status", "-s", "--reset", "-r",
        "--interactive",
    };

    /// <summary>argv를 파싱한다. parse error면 null 반환.</summary>
    public static CommandContext? Parse(string[] argv)
    {
        // ─── env vars ────────────────────────────────────────────────────────
        var envMaxRetries = TryParseInt(Environment.GetEnvironmentVariable("MAX_RETRIES"));
        var envRetryDelay = TryParseInt(Environment.GetEnvironmentVariable("RETRY_DELAY"));
        var envMaxParallel = TryParseInt(Environment.GetEnvironmentVariable("RALPH_MAX_PARALLEL")) ?? 0;
        var envParallelDisabled = string.Equals(
            Environment.GetEnvironmentVariable("RALPH_PARALLEL"), "false",
            StringComparison.OrdinalIgnoreCase);
        var envStrictFiles = string.Equals(
            Environment.GetEnvironmentVariable("RALPH_STRICT_FILES"), "true",
            StringComparison.OrdinalIgnoreCase);
        var envNoSmokeTestRaw = Environment.GetEnvironmentVariable("RALPH_NO_SMOKE_TEST")?.ToLowerInvariant();
        var envNoSmokeTest = envNoSmokeTestRaw is "true" or "1";
        var envSharedRaw = Environment.GetEnvironmentVariable("RALPH_SHARED_WORKTREES")?.ToLowerInvariant();
        bool? envSharedWorktrees = envSharedRaw switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => null,
        };
        var envBudgetUsd = TryParseDouble(Environment.GetEnvironmentVariable("RALPH_BUDGET_USD"));
        var envTaskTimeoutSecRaw = TryParseInt(Environment.GetEnvironmentVariable("RALPH_TASK_TIMEOUT_SEC"));
        var envTaskTimeoutSec = envTaskTimeoutSecRaw is > 0 ? envTaskTimeoutSecRaw : null;
        var envSmokeTestCommandRaw = Environment.GetEnvironmentVariable("RALPH_SMOKE_TEST_COMMAND");
        var envSmokeTestCommand = string.IsNullOrWhiteSpace(envSmokeTestCommandRaw) ? null : envSmokeTestCommandRaw;

        // ─── CLI flags (boolean) ─────────────────────────────────────────────
        var argList = argv.ToList();
        var debug = argList.Remove("--debug");
        var sequential = argList.Remove("--sequential");
        var forceFlag = argList.Remove("--force");
        var cliStrictFiles = argList.Remove("--strict-files");
        var cliSharedWorktrees = argList.Remove("--shared-worktrees");
        var cliNoSmokeTest = argList.Remove("--no-smoke-test");
        var llmCritique = argList.Remove("--llm-critique");

        // ─── --max-parallel ─────────────────────────────────────────────────
        var maxParallelArg = 0;
        var mpIdx = argList.IndexOf("--max-parallel");
        if (mpIdx >= 0)
        {
            if (mpIdx + 1 >= argList.Count)
            {
                AnsiConsole.MarkupLine("[red]Error: --max-parallel 값이 누락되었습니다 (양의 정수 필요).[/]");
                return null;
            }
            var raw = argList[mpIdx + 1];
            if (!int.TryParse(raw, out maxParallelArg) || maxParallelArg <= 0)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Error: --max-parallel 값을 파싱할 수 없습니다: '{Markup.Escape(raw)}' (양의 정수 필요)[/]");
                return null;
            }
            argList.RemoveRange(mpIdx, 2);
        }

        // ─── --budget-usd ────────────────────────────────────────────────────
        double? cliBudgetUsd = null;
        var budgetIdx = argList.IndexOf("--budget-usd");
        if (budgetIdx >= 0 && budgetIdx + 1 < argList.Count)
        {
            var raw = argList[budgetIdx + 1];
            if (double.TryParse(raw,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var bv))
            {
                cliBudgetUsd = bv;
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[red]Error: --budget-usd 값을 파싱할 수 없습니다: '{Markup.Escape(raw)}'[/]");
                return null;
            }
            argList.RemoveRange(budgetIdx, 2);
        }

        // ─── --smoke-test ────────────────────────────────────────────────────
        // 1회용 smoke test 명령 override. workflow.smokeTest와 자동 추론을 모두 우회.
        // 예: ralph --run --smoke-test "pnpm build && pnpm test"
        string? cliSmokeTestCommand = null;
        var stIdx = argList.IndexOf("--smoke-test");
        if (stIdx >= 0)
        {
            if (stIdx + 1 >= argList.Count)
            {
                AnsiConsole.MarkupLine("[red]Error: --smoke-test 값이 누락되었습니다 (셸 명령 문자열 필요).[/]");
                return null;
            }
            var raw = argList[stIdx + 1];
            if (string.IsNullOrWhiteSpace(raw))
            {
                AnsiConsole.MarkupLine("[red]Error: --smoke-test 값이 비어 있습니다.[/]");
                return null;
            }
            cliSmokeTestCommand = raw;
            argList.RemoveRange(stIdx, 2);
        }

        // ─── --task-timeout ──────────────────────────────────────────────────
        int? cliTaskTimeoutSec = null;
        var ttIdx = argList.IndexOf("--task-timeout");
        if (ttIdx >= 0 && ttIdx + 1 < argList.Count)
        {
            var raw = argList[ttIdx + 1];
            if (DurationParser.TryParseSeconds(raw, out var parsed) && parsed > 0)
            {
                cliTaskTimeoutSec = parsed;
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[red]Error: --task-timeout 값을 파싱할 수 없습니다: '{Markup.Escape(raw)}' (예: 30m, 1h, 90s, 1800)[/]");
                return null;
            }
            argList.RemoveRange(ttIdx, 2);
        }

        // ─── --model ─────────────────────────────────────────────────────────
        var modelArg = "opus";
        var modelIdx = argList.IndexOf("--model");
        if (modelIdx >= 0 && modelIdx + 1 < argList.Count)
        {
            var modelValue = argList[modelIdx + 1].ToLowerInvariant();
            if (modelValue is "sonnet" or "opus")
            {
                modelArg = modelValue;
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Error: Invalid model '{Markup.Escape(modelValue)}'. Allowed: sonnet, opus[/]");
                return null;
            }
            argList.RemoveRange(modelIdx, 2);
        }

        // ─── tasks file resolution ───────────────────────────────────────────
        var tasksFile = "tasks.json";

        var fileIdx = argList.IndexOf("--file");
        if (fileIdx < 0) fileIdx = argList.IndexOf("-f");
        if (fileIdx >= 0 && fileIdx + 1 < argList.Count)
        {
            tasksFile = argList[fileIdx + 1];
            argList.RemoveRange(fileIdx, 2);
        }
        else if (argList.Count > 1
                 && FilePositionalCommands.Contains(argList[0])
                 && !argList[1].StartsWith("--"))
        {
            tasksFile = argList[1];
        }

        var command = argList.Count > 0 ? argList[0] : "";

        return new CommandContext
        {
            Command = command,
            Args = argList,
            Debug = debug,
            Sequential = sequential,
            ForceFlag = forceFlag,
            CliStrictFiles = cliStrictFiles,
            CliSharedWorktrees = cliSharedWorktrees,
            CliNoSmokeTest = cliNoSmokeTest,
            LlmCritique = llmCritique,
            MaxParallelArg = maxParallelArg,
            ModelArg = modelArg,
            TasksFile = tasksFile,
            CliBudgetUsd = cliBudgetUsd,
            CliTaskTimeoutSec = cliTaskTimeoutSec,
            CliSmokeTestCommand = cliSmokeTestCommand,
            EnvMaxRetries = envMaxRetries,
            EnvRetryDelay = envRetryDelay,
            EnvMaxParallel = envMaxParallel,
            EnvParallelDisabled = envParallelDisabled,
            EnvStrictFiles = envStrictFiles,
            EnvNoSmokeTest = envNoSmokeTest,
            EnvSharedWorktrees = envSharedWorktrees,
            EnvBudgetUsd = envBudgetUsd,
            EnvTaskTimeoutSec = envTaskTimeoutSec,
            EnvSmokeTestCommand = envSmokeTestCommand,
        };
    }

    private static int? TryParseInt(string? s) =>
        int.TryParse(s, out var v) ? v : (int?)null;

    private static double? TryParseDouble(string? s) =>
        double.TryParse(s,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : (double?)null;
}
