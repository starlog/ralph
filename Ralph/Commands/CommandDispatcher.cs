namespace Ralph.Commands;

/// <summary>
/// Command name → ICommand 매핑. 새 핸들러는 여기에 한 줄 추가.
/// 알 수 없는 명령은 <see cref="UnknownCommand"/>으로 라우팅.
/// </summary>
public static class CommandDispatcher
{
    public static ICommand Resolve(CommandContext ctx) => ctx.Command switch
    {
        "--plan"               => new PlanCommand(ctx),
        "--plan-prompt"        => new PlanPromptCommand(ctx),
        "--run"                => new RunCommand(ctx),
        "--dry-run"            => new DryRunCommand(ctx),
        "--task"               => new SingleTaskCommand(ctx),
        "--interactive"        => new InteractiveCommand(ctx),
        "--list" or "-l"       => new ListCommand(ctx),
        "--graph" or "-g"      => new GraphCommand(ctx),
        "--prompts" or "-p"    => new PromptsCommand(ctx),
        "--status" or "-s"     => new StatusCommand(ctx),
        "--reset" or "-r"      => new ResetCommand(ctx),
        "--logs"               => new LogsCommand(ctx),
        "--cost"               => new CostCommand(),
        "--show-prompt"        => new ShowPromptCommand(ctx),
        "--validate"           => new ValidateCommand(ctx),
        "--critique"           => new CritiqueCommand(ctx),
        "--worktree-cleanup"   => new WorktreeCleanupCommand(),
        "--version" or "-v"    => new VersionCommand(),
        "--help" or "-h" or "" => new HelpCommand(),
        _                      => new UnknownCommand(ctx.Command),
    };
}
