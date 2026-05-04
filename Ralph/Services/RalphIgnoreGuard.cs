using Spectre.Console;

namespace Ralph.Services;

/// <summary>
/// Ralph가 만든 artifact 디렉터리(<see cref="RalphPaths.LogDir"/>,
/// <see cref="RalphPaths.WorktreeDir"/>, <see cref="RalphPaths.SmokeWorktreeDir"/>)가
/// git에 의해 추적되지 않도록 보장한다.
///
/// <para>
/// 동기: <c>.ralph-smoke</c>는 detached worktree이므로 자체 <c>.git</c> 파일을 가진다.
/// 사용자가 무심코 <c>git add .</c>를 돌리면 git이 이를 gitlink(mode 160000) 서브모듈
/// 엔트리로 인덱스에 기록한다. 이후 ralph가 매 batch마다 smoke worktree HEAD를
/// <c>git reset --hard baseBranch</c>로 갱신하면 부모 인덱스의 gitlink SHA와 어긋나
/// <c>M .ralph-smoke (new commits)</c>로 보이고, batch의 모든 task가 rebase
/// preflight 단계에서 일괄 실패한다.
/// </para>
///
/// <para>
/// 방어 전략 두 단계:
/// <list type="number">
/// <item><description>
/// <c>.git/info/exclude</c>(local-only, 절대 commit 안 됨)에 ralph artifact 경로를
/// idempotent하게 append. <c>.gitignore</c>를 건드리지 않는 이유: tracked file이라
/// 무관한 변경을 일으키고, 사용자 의도와 충돌할 수 있다.
/// </description></item>
/// <item><description>
/// 이미 tracked 상태(특히 gitlink)로 들어가 있으면 명확한 메시지로 fail-fast.
/// silent <c>git rm --cached</c>는 working tree를 staged-deletion 상태로 만들어
/// 후속 rebase preflight를 또 깨뜨리므로 사용자가 직접 commit해야 한다.
/// </description></item>
/// </list>
/// </para>
/// </summary>
public static class RalphIgnoreGuard
{
    private static readonly string[] ManagedSegments = new[]
    {
        RalphPaths.LogDir,
        RalphPaths.WorktreeDir,
        RalphPaths.SmokeWorktreeDir,
    };

    private const string ExcludeHeader =
        "# Added by ralph — local-only excludes for runtime artifact dirs (do not remove)";

    /// <summary>
    /// Guard를 실행한다. 호출 사이트:
    /// <see cref="WorktreeService.CreateWorktreeAsync"/>,
    /// <see cref="WorktreeService.EnsureSmokeWorktreeAsync"/>.
    /// 두 진입점에서 공통으로 호출해도 idempotent하다.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// ralph artifact 경로가 tracked로 인덱스에 들어가 있을 때. 메시지는 사용자가
    /// 직접 따라 할 수 있는 명령(<c>git rm --cached</c> + commit)을 포함한다.
    /// </exception>
    public static async Task EnsureAsync(
        GitService git, string repoRoot, RalphLogger? logger = null,
        CancellationToken ct = default)
    {
        logger ??= RalphLogger.Null;

        await EnsureExcludeFileAsync(git, repoRoot, logger, ct);
        await DetectTrackedAsync(git, repoRoot, logger, ct);
    }

    /// <summary>
    /// <c>.git/info/exclude</c>에 누락된 ralph artifact 라인을 append한다. 파일이 없으면
    /// 만든다. 이미 모두 들어 있으면 IO 없이 반환한다 (대부분의 호출에서 hit).
    /// </summary>
    private static async Task EnsureExcludeFileAsync(
        GitService git, string repoRoot, RalphLogger logger, CancellationToken ct)
    {
        // git common dir resolution — 워크트리에서 호출되어도 main .git을 가리킨다.
        var (cdExit, cdOut) = await git.RunAsync(
            ["rev-parse", "--git-common-dir"], repoRoot, ct);
        if (cdExit != 0)
        {
            // git이 동작 못하는 환경 — 그냥 조용히 포기. dependency check가 이미 통과했으니
            // 흔한 케이스는 아님.
            logger.Warn($"[ralph-ignore] git-common-dir 조회 실패 — exclude 갱신 스킵: {cdOut.Trim()}");
            return;
        }

        var commonDir = cdOut.Trim();
        if (!Path.IsPathRooted(commonDir))
            commonDir = Path.GetFullPath(Path.Combine(repoRoot, commonDir));

        var infoDir = Path.Combine(commonDir, "info");
        var excludePath = Path.Combine(infoDir, "exclude");

        // 누락된 라인만 추려낸다 — 이미 있는 라인은 그대로 둔다.
        var existing = File.Exists(excludePath)
            ? await File.ReadAllLinesAsync(excludePath, ct)
            : Array.Empty<string>();

        var existingSet = new HashSet<string>(
            existing.Select(l => l.Trim()), StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var seg in ManagedSegments)
        {
            // gitignore semantics: trailing slash = directory only.
            var line = seg + "/";
            if (!existingSet.Contains(line))
                missing.Add(line);
        }

        if (missing.Count == 0) return;

        Directory.CreateDirectory(infoDir);

        // 끝 개행 없이 끝나면 한 줄 띄워서 append, 그렇지 않으면 그대로 이어서.
        var sb = new System.Text.StringBuilder();
        if (existing.Length > 0)
        {
            // 마지막 라인까지 있는 그대로 보존하고, 새 블록은 빈 줄로 분리.
            sb.AppendJoin('\n', existing);
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
            sb.Append('\n');
        }
        sb.AppendLine(ExcludeHeader);
        foreach (var line in missing) sb.AppendLine(line);

        await File.WriteAllTextAsync(excludePath, sb.ToString(), ct);
        logger.Info(
            $"[ralph-ignore] {excludePath}에 {missing.Count}개 라인 추가: " +
            string.Join(", ", missing));
    }

    /// <summary>
    /// <c>git ls-files</c>로 ralph artifact 경로 산하의 추적 엔트리를 검사한다.
    /// 하나라도 발견되면 <see cref="InvalidOperationException"/>으로 fail-fast.
    /// </summary>
    private static async Task DetectTrackedAsync(
        GitService git, string repoRoot, RalphLogger logger, CancellationToken ct)
    {
        var tracked = new List<string>();
        foreach (var seg in ManagedSegments)
        {
            // ls-files는 tracked만 보고, 디렉터리 인자에 대해서는 그 산하 파일을 나열한다.
            // gitlink(서브모듈/별도 worktree로 add된 디렉터리)도 한 줄로 나온다.
            var (exit, output) = await git.RunAsync(
                ["ls-files", "--", seg], repoRoot, ct);
            if (exit != 0) continue; // 경로가 인덱스에 없으면 0, 빈 output. 다른 실패는 조용히 패스.
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            tracked.AddRange(lines);
        }

        if (tracked.Count == 0) return;

        var preview = tracked.Take(5).ToList();
        var more = tracked.Count - preview.Count;

        var msg =
            "ralph가 만드는 artifact 경로(.ralph-logs / .ralph-worktrees / .ralph-smoke)가 git에 추적 중입니다. " +
            ".ralph-smoke가 gitlink로 추적되면 매 batch에서 rebase preflight가 더티 트리로 차단되어 모든 task가 실패합니다.\n\n" +
            "추적 중인 엔트리:\n  " + string.Join("\n  ", preview) +
            (more > 0 ? $"\n  ... (+{more}개)" : "") +
            "\n\n해결 (한 번만 실행):\n" +
            "  git rm --cached -r --ignore-unmatch " + string.Join(' ', ManagedSegments) + "\n" +
            "  git commit -m \"chore: ralph artifact 경로 untrack\"\n\n" +
            "이후 ralph가 .git/info/exclude에 이 경로들을 자동 등록해 재발을 막습니다.";

        logger.Error("[ralph-ignore] tracked artifact entries: " + string.Join(", ", tracked));
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(msg)}[/]");

        throw new RalphUserException(msg);
    }
}
