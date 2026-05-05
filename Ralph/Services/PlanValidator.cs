using System.Text;
using System.Text.RegularExpressions;
using Ralph.Models;
using Spectre.Console;

namespace Ralph.Services;

public class PlanValidationReport
{
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];

    public bool HasErrors => Errors.Count > 0;
    public bool HasWarnings => Warnings.Count > 0;

    public bool IsClean => !HasErrors && !HasWarnings;
}

/// <summary>
/// tasks.json의 무결성·일관성 검증. PlanGenerator 직후, --run 시작 직전,
/// 또는 ralph --validate로 단독 실행됩니다.
/// </summary>
public static class PlanValidator
{
    private static readonly string[] SensitivePatterns =
        [".env", ".pem", ".key", ".p12", ".pfx", "credentials.json", "id_rsa", "id_ed25519"];

    /// <summary>
    /// `<interpreter> <eval-flag> "..."` 또는 `'...'` 패턴을 잡아 quoted body를 캡처합니다.
    /// 인터프리터: python/python3, node/nodejs, bun, ruby, perl, php, lua, Rscript.
    /// 평가 flag: `-c`, `-e`, `-E`, `-r`(php), `-p`/`--print`(node), `--eval`.
    /// </summary>
    private static readonly Regex InlineScriptPattern = new(
        @"(?<!\w)(python3?|node|nodejs|bun|ruby|perl|php|lua|Rscript)\b\s+(?:-c|-e|-E|-r|-p|--eval|--print)\s+(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)')",
        RegexOptions.Compiled);

    // ---- fix2 #3: verification.command 인젝션 표면 검사용 패턴 ----
    // VerificationRunner 가 명령을 `/bin/sh -c` (POSIX) / `cmd /c` (Windows) 로 그대로 실행하므로,
    // LLM 이 PRD 의 prompt-injection 을 옮길 경우 임의 실행이 가능하다. 명백한 우회 기법은
    // errors 로 차단하여 PlanCommand 의 자동 보정 루프(BuildCorrectionPrompt)를 유도한다.

    /// <summary>`curl|wget|fetch ... | sh|bash|zsh` 같은 다운로드+즉시 실행 파이프.</summary>
    private static readonly Regex CurlPipeShellPattern = new(
        @"\b(?:curl|wget|fetch)\b[^|]*\|\s*(?:sudo\s+)?(?:sh|bash|zsh)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>`eval`/`source` 의 동적 평가. word-boundary 로 medieval 같은 단어는 제외.</summary>
    private static readonly Regex EvalSourcePattern = new(
        @"(?<![\w.\-/])(?:eval|source)\s+\S",
        RegexOptions.Compiled);

    /// <summary>외부 명령 출력으로 합성된 `$(curl ...)`, `$(cat /etc/...)` 등.</summary>
    private static readonly Regex CmdSubExternPattern = new(
        @"\$\(\s*(?:curl|wget|fetch|cat\s+/(?:etc|root|home)\b|env\b|printenv\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>backtick 안에 외부 fetch / `cat /etc` 등.</summary>
    private static readonly Regex BacktickExternPattern = new(
        @"`[^`]*\b(?:curl|wget|fetch|cat\s+/(?:etc|root|home)|env|printenv)\b[^`]*`",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>민감 경로(.env / .ssh / .aws / *.pem / authorized_keys 등) 로의 redirect.</summary>
    private static readonly Regex RedirSensitivePattern = new(
        @"(?:>>?|<)\s*(?:~/\.?ssh\b|~/\.aws\b|~/\.config\b|/etc/|/root/|\.env(?:\.\w+)?\b|\S+\.(?:pem|key|p12|pfx)\b|credentials\.json\b|id_rsa\b|id_ed25519\b|authorized_keys\b)",
        RegexOptions.Compiled);

    /// <summary>heredoc 내부에 `$(...)`/backtick/`${...}` 가 동시에 등장.</summary>
    private static readonly Regex HeredocWithSubPattern = new(
        @"<<-?\s*[A-Za-z_]\w*[\s\S]*?(?:\$\(|`|\$\{)",
        RegexOptions.Compiled);

    /// <summary>$HOME / $USER / $USERPROFILE 등 home-dir env 변수를 통한 worktree 외부 경로 접근.</summary>
    private static readonly Regex EnvHomeEscapePattern = new(
        @"\$\{?(?:HOME|USERPROFILE|HOMEPATH|HOMEDRIVE|USER|LOGNAME)\}?(?:/|\\|\b)",
        RegexOptions.Compiled);

    /// <summary>`cd $TARGET`, `>>$LOG` 같은 일반 환경변수 path arg — worktree 격리 침범 가능.</summary>
    private static readonly Regex EnvPathEscapePattern = new(
        @"(?:\bcd\s+|>>?\s*|<\s*)\$\{?[A-Z_][A-Z0-9_]*\}?",
        RegexOptions.Compiled);

    /// <summary>VC-CMDSUB-EXTERN 에 매치되지 않은 잔여 일반 `$(...)` 동적 치환.</summary>
    private static readonly Regex GenericCmdSubPattern = new(@"\$\([^)]+\)", RegexOptions.Compiled);

    /// <summary>VC-BACKTICK-EXTERN 에 매치되지 않은 잔여 일반 backtick 치환.</summary>
    private static readonly Regex GenericBacktickPattern = new(@"`[^`]+`", RegexOptions.Compiled);

    /// <summary>
    /// `npx <tool>` (또는 `pnpx`/`bunx`) 호출의 첫 도구 이름을 캡처. 옵션 `--package &lt;name&gt;`는
    /// skip 한다. 개선 D — worktree에 node_modules가 없어 cold install 비용이 누적되는 패턴 검출.
    /// </summary>
    private static readonly Regex NpxRunnerPattern = new(
        @"(?<![\w.\-/])(?:npx|pnpx|bunx)\s+(?:--package\s+\S+\s+)?(?:-y\s+|--yes\s+)?([\w@][-\w/]*)",
        RegexOptions.Compiled);

    /// <summary>
    /// 같은 명령 안에 자기-부트스트랩 install 단계가 포함되어 있는지 검사. 있으면 npx 호출이
    /// cold install이 아니라 lockfile 기반 결정론적 install 후 실행되므로 경고를 건너뛴다.
    /// 패턴: `npm ci`, `npm install`, `pnpm install`, `pnpm i`, `yarn install`, `yarn` (인자 없는),
    /// `bun install`. 또한 `test -d node_modules || npm ci` 같은 가드도 자연스럽게 포함된다 —
    /// 명령 어디에든 install 토큰이 등장하면 OK로 본다.
    /// </summary>
    private static readonly Regex SelfBootstrapInstallPattern = new(
        @"(?<![\w.\-/])(?:npm\s+(?:ci|install)|pnpm\s+(?:install|i)\b|yarn\s+install|bun\s+install)",
        RegexOptions.Compiled);

    /// <summary>
    /// 보편적인 npm/yarn/pnpm/bun lockfile 이름 — package.json 을 만드는 task가 lockfile을
    /// outputFiles에 선언하지 않으면 머지 후 base에 lockfile이 빠져 후속 worktree가 `npm ci`로
    /// 결정론적 install을 못한다 (cold install 누적). 개선 D 보조 검사.
    /// </summary>
    private static readonly string[] NodeLockfileNames =
        ["package-lock.json", "yarn.lock", "pnpm-lock.yaml", "bun.lockb"];

    /// <summary>
    /// verification.command / workflow.smokeTest.command에서 declared 검사를 위해
    /// "이 토큰은 source 파일 경로다" 라고 인식할 확장자 화이트리스트.
    /// 컴파일/빌드 매니페스트(.csproj/.sln/.fsproj)도 포함 — `dotnet build src/Foo/Foo.csproj`
    /// 같은 호출도 declared scope 안에 있어야 한다.
    /// </summary>
    private static readonly HashSet<string> SourceFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".vue", ".svelte",
        ".py", ".pyi",
        ".go", ".rs",
        ".cs", ".fs", ".vb", ".csproj", ".fsproj", ".vbproj", ".sln",
        ".java", ".kt", ".kts", ".scala",
        ".rb", ".php", ".swift", ".m", ".mm",
        ".cpp", ".cc", ".cxx", ".c", ".h", ".hpp", ".hh",
        ".lua", ".dart", ".ex", ".exs", ".clj", ".cljs",
        ".sql", ".proto", ".graphql",
    };

    /// <summary>알려진 빌드/테스트 러너. 첫 토큰이 여기에 없으면 [info] 권장 메시지를 추가한다.</summary>
    private static readonly string[] WhitelistedTools =
    [
        "dotnet", "npm", "npx", "pnpm", "pnpx", "yarn", "bun", "bunx", "node", "nodejs",
        "cargo", "rustc", "go",
        "pytest", "python", "python3", "ruff", "mypy",
        "bash", "sh", "zsh",
        "ruby", "rake", "bundle",
        "mvn", "gradle", "./gradlew",
        "make", "cmake", "ninja",
        "ctest", "jest", "vitest", "mocha", "phpunit",
        "git", "docker", "kubectl", "terraform",
        "tsc", "eslint", "prettier",
        "echo", "true", "false",
    ];

    public static PlanValidationReport Validate(TaskManager tm)
    {
        var report = new PlanValidationReport();
        var tasks = tm.Data.Tasks;
        var idSet = tasks.Select(t => t.Id).ToHashSet();

        // 1. ID 중복
        var duplicates = tasks.GroupBy(t => t.Id).Where(g => g.Count() > 1).Select(g => g.Key);
        foreach (var d in duplicates)
            report.Errors.Add($"중복된 task ID: '{d}'");

        // 2. DAG cycle
        if (tm.HasCycle(out var cycle))
            report.Errors.Add($"순환 의존성: {string.Join(" → ", cycle)}");

        // 3. dependsOn 참조 무결성
        foreach (var task in tasks)
        {
            if (task.DependsOn is not { Count: > 0 }) continue;
            foreach (var dep in task.DependsOn)
            {
                if (!idSet.Contains(dep))
                    report.Errors.Add($"'{task.Id}' → 존재하지 않는 의존 task '{dep}'를 참조합니다");
                if (dep == task.Id)
                    report.Errors.Add($"'{task.Id}'가 자기 자신에 의존합니다");
            }
        }

        // 4. 필수 필드
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Id))
                report.Errors.Add("id가 비어있는 task가 있습니다");
            if (string.IsNullOrWhiteSpace(task.Title))
                report.Errors.Add($"'{task.Id}'의 title이 비어있습니다");
            if (string.IsNullOrWhiteSpace(task.Prompt))
                report.Errors.Add(
                    $"'{task.Id}'의 prompt가 비어있습니다 — task가 의미 있는 작업 지시를 가져야 합니다");
        }

        // 5. modifiedFiles overlap — 서로 의존이 없는 task 쌍이 같은 파일을 수정하면 병렬 시 충돌 가능
        var fileMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (task.ModifiedFiles is { Count: > 0 }) files.UnionWith(task.ModifiedFiles);
            if (task.OutputFiles is { Count: > 0 }) files.UnionWith(task.OutputFiles);
            foreach (var f in files)
            {
                if (!fileMap.TryGetValue(f, out var list))
                    fileMap[f] = list = [];
                list.Add(task.Id);
            }
        }
        foreach (var (file, taskIds) in fileMap)
        {
            if (taskIds.Count < 2) continue;
            // 두 태스크 사이에 의존 경로가 없는 경우만 경고
            for (var i = 0; i < taskIds.Count; i++)
            {
                for (var j = i + 1; j < taskIds.Count; j++)
                {
                    var a = taskIds[i];
                    var b = taskIds[j];
                    if (!HasDependencyPath(tasks, a, b) && !HasDependencyPath(tasks, b, a))
                    {
                        report.Warnings.Add(
                            $"'{a}'와 '{b}'가 같은 파일 '{file}'을 수정하지만 서로 의존이 없습니다 → 병렬 실행 시 머지 충돌 위험");
                    }
                }
            }
        }

        // 6. category-prompt 정합성 (간이 휴리스틱). workflow.categories를 customize한 프로젝트에서는
        //    아래의 plan/test/commit 키워드 가정이 맞지 않을 수 있어 default 4-stage 사용 시에만 적용.
        var configuredCats = tm.Data.Workflow?.Categories;
        var usingDefaultCategories = configuredCats is null || configuredCats.Count == 0;
        if (usingDefaultCategories)
        {
            foreach (var task in tasks)
            {
                if (string.IsNullOrWhiteSpace(task.Prompt) || string.IsNullOrWhiteSpace(task.Category)) continue;
                var lower = task.Prompt.ToLowerInvariant();
                switch (task.Category)
                {
                    case "test" or "testing":
                        if (!lower.Contains("test") && !lower.Contains("테스트") && !lower.Contains("검증"))
                            report.Warnings.Add($"'{task.Id}' (category=testing)의 prompt에 test/테스트/검증 키워드가 없습니다");
                        break;
                    case "commit":
                        if (!lower.Contains("commit") && !lower.Contains("커밋") && !lower.Contains("git "))
                            report.Warnings.Add($"'{task.Id}' (category=commit)의 prompt에 commit/커밋/git 키워드가 없습니다");
                        break;
                    case "plan":
                        if (!lower.Contains("plan") && !lower.Contains("계획") && !lower.Contains("설계") && !lower.Contains("분석"))
                            report.Warnings.Add($"'{task.Id}' (category=plan)의 prompt에 plan/계획/설계/분석 키워드가 없습니다");
                        break;
                }
            }
        }

        // 7. verification.command anti-pattern: `\n`/`\t`/`\r` 리터럴이 다음 두 위치 중 하나에 있는 경우 error.
        //    (a) shell 레벨 — 명령 전체가 `set -e\ncd ...\n...` 처럼 multi-line shell script로 작성된 경우.
        //        ralph가 `/bin/sh -c "<command>"`로 실행하므로 shell은 backslash+n을 LF로 변환하지 않음.
        //    (b) 인터프리터 레벨 — `<lang> -c "...\n..."` 안의 quoted body. 같은 이유로 인터프리터가 SyntaxError.
        //    두 스캔 모두 string-literal-aware하여 정상적인 string literal 안의 `\n`은 false positive 없이 통과.
        foreach (var task in tasks)
        {
            var cmd = task.Verification?.Command;
            if (string.IsNullOrWhiteSpace(cmd)) continue;

            // (a) shell-level scan — quoted string 밖의 top-level `\n`을 잡음
            if (ContainsBadEscape(cmd))
            {
                report.Errors.Add(
                    $"'{task.Id}' verification.command에 top-level `\\n`/`\\t`/`\\r` 이스케이프가 있습니다 " +
                    "(예: `set -e\\ncd ...\\nout=...`). ralph는 명령을 `/bin/sh -c`로 실행하는데, " +
                    "shell은 backslash+n을 개행으로 변환하지 않습니다. multi-line script가 필요하면 " +
                    "별도 `.sh` 파일로 저장 후 `bash path/to/script.sh`로 호출하거나, 명령 전체를 " +
                    "`bash -c $'set -e\\ncd ...'` 처럼 ANSI-C quoting으로 감싸세요.");
                continue; // shell-level error가 이미 있으면 인터프리터 레벨까지 보고할 필요 없음
            }

            // (b) interpreter-level scan — `<lang> -c|-e|--eval "..."` 본문
            foreach (Match m in InlineScriptPattern.Matches(cmd))
            {
                // bash ANSI-C quoting `$'...'` 은 \n을 실제 개행으로 확장하므로 안전 — skip.
                var quoteGroup = m.Groups["dq"].Success ? m.Groups["dq"] : m.Groups["sq"];
                var quoteStart = quoteGroup.Index - 1; // 여는 따옴표 위치
                if (quoteStart > 0 && cmd[quoteStart - 1] == '$') continue;

                var body = quoteGroup.Value;
                if (ContainsBadEscape(body))
                {
                    var lang = m.Groups[1].Value;
                    report.Errors.Add(
                        $"'{task.Id}' verification.command: `{lang} -c/-e/--eval` 안에 `\\n`/`\\t`/`\\r` 이스케이프가 있습니다. " +
                        "shell은 따옴표 안의 `\\n`을 개행으로 변환하지 않아 인터프리터가 SyntaxError를 일으킵니다. " +
                        "단일 statement(`;` 구분)나 프로젝트 표준 테스트 러너(예: `pytest -q`, `npm test`)를 사용하세요.");
                }
            }
        }

        // 7.5. verification.command 셸 인젝션 표면 검사 (fix2 #3).
        //      VerificationRunner는 명령을 `/bin/sh -c` (POSIX) / `cmd /c` (Windows)로 그대로 실행하므로,
        //      LLM이 PRD의 prompt-injection 지시를 옮길 경우 임의 실행이 가능하다.
        //      명백한 우회 기법은 errors로 차단해 자동 보정 루프(BuildCorrectionPrompt)를 유도하고,
        //      모호한 dynamic 치환은 warnings로 사용자가 결정한다.
        //      모든 패턴은 string-literal-aware 하게 (StripStringLiterals) 검사하여 정상 인터프리터
        //      string literal 내부의 `$(...)` / 민감 토큰은 false positive 로 잡지 않는다.
        foreach (var task in tasks)
        {
            var cmd = task.Verification?.Command;
            if (string.IsNullOrWhiteSpace(cmd)) continue;

            var topLevel = StripStringLiterals(cmd);
            var errorsBefore = report.Errors.Count;

            // --- errors: 명백한 우회 기법 ---
            if (CurlPipeShellPattern.IsMatch(topLevel))
                report.Errors.Add(
                    $"'{task.Id}' verification.command: 외부 스크립트를 즉시 실행하는 `curl|sh` / `wget|bash` 패턴이 감지되어 차단됩니다. " +
                    "검증은 저장소에 체크인된 빌드/테스트 러너(예: `dotnet test`, `npm test`, `pytest -q`)로만 실행하세요.");

            if (EvalSourcePattern.IsMatch(topLevel))
                report.Errors.Add(
                    $"'{task.Id}' verification.command: `eval`/`source` 동적 평가가 감지되어 차단됩니다 (외부 입력으로 명령을 합성할 수 있어 임의 실행 위험). " +
                    "정적인 단일 명령(예: `dotnet test`, `pytest -q`)으로 교체하세요.");

            if (CmdSubExternPattern.IsMatch(topLevel))
                report.Errors.Add(
                    $"'{task.Id}' verification.command: 외부 명령 출력으로 합성된 `$(curl ...)` / `$(cat /etc/...)` 류가 감지되어 차단됩니다. " +
                    "검증 명령은 정적인 인자만 사용해야 하며, 동적 치환이 필요하면 빌드 스크립트로 분리하세요.");

            if (BacktickExternPattern.IsMatch(topLevel))
                report.Errors.Add(
                    $"'{task.Id}' verification.command: backtick 안에 외부 fetch(`curl`/`wget`) 또는 `cat /etc/...` 가 감지되어 차단됩니다. " +
                    "정적인 단일 명령으로 교체하세요.");

            if (RedirSensitivePattern.IsMatch(topLevel))
                report.Errors.Add(
                    $"'{task.Id}' verification.command: 민감 경로(.env / .ssh / .aws / *.pem / *.key / authorized_keys / /etc / /root)로의 `>` / `>>` redirect 가 감지되어 차단됩니다. " +
                    "검증 명령은 stdout/stderr 로 결과만 내야 하며, 산출물이 필요하면 빌드 디렉터리(`bin/`, `dist/` 등)로 쓰세요.");

            if (HeredocWithSubPattern.IsMatch(topLevel))
                report.Errors.Add(
                    $"'{task.Id}' verification.command: heredoc 본문에 `$(...)` / backtick / `${{...}}` 가 있어 명령 합성 통로가 됩니다 — 차단됩니다. " +
                    "heredoc 대신 정적인 단일 명령을 사용하거나, 별도 `.sh` 파일로 분리해 `bash path/to/script.sh` 로 호출하세요.");

            if (EnvHomeEscapePattern.IsMatch(topLevel))
                report.Errors.Add(
                    $"'{task.Id}' verification.command: `$HOME`/`$USER`/`$USERPROFILE` 등 home-dir 환경변수를 통한 경로 접근이 감지되어 차단됩니다 " +
                    "(예: `$HOME/.ssh/...`). verification.command는 worktree 안에서 실행되며 home 경로를 참조해서는 안 됩니다.");

            var taskHadInjectionError = report.Errors.Count > errorsBefore;

            // --- warnings: 의심스럽지만 정상 사용 가능한 dynamic 패턴 ---
            if (EnvPathEscapePattern.IsMatch(topLevel))
                report.Warnings.Add(
                    $"'{task.Id}' verification.command: cd 또는 redirect 의 대상이 환경 변수입니다 — worktree 밖으로 빠질 수 있어 의도된 사용인지 확인하세요.");

            if (GenericCmdSubPattern.IsMatch(topLevel) && !CmdSubExternPattern.IsMatch(topLevel))
                report.Warnings.Add(
                    $"'{task.Id}' verification.command 에 `$(...)` 동적 치환이 있습니다 — 의도된 사용인지 확인하고, 가능하면 정적 인자로 교체하세요.");

            if (GenericBacktickPattern.IsMatch(topLevel) && !BacktickExternPattern.IsMatch(topLevel))
                report.Warnings.Add(
                    $"'{task.Id}' verification.command 에 backtick 동적 치환이 있습니다 — 의도된 사용인지 확인하세요.");

            if (CountStatements(topLevel) >= 5)
                report.Warnings.Add(
                    $"'{task.Id}' verification.command 가 5개 이상의 statement 로 구성되어 있습니다 — 단일 빌드/테스트 호출(`dotnet test`, `npm test` 등)로 단순화를 권장합니다.");

            // --- info: errors 가 없고 명령 어디에도 화이트리스트 러너가 없으면 권장 메시지.
            //     `test -d node_modules || npm ci; npx tsc ...` 같은 guard 패턴은 첫 토큰이 `test`라
            //     화이트리스트 미스이지만 체인 안에 npx/npm이 있으므로 의미 없는 경고. 명령 어디에든
            //     known runner 토큰이 등장하면 [info] 자체를 생략한다.
            if (!taskHadInjectionError && !ContainsAnyWhitelistedTool(topLevel))
            {
                var first = ExtractFirstToken(cmd);
                if (first is not null
                    && !WhitelistedTools.Contains(first, StringComparer.OrdinalIgnoreCase))
                {
                    report.Warnings.Add(
                        $"[info] '{task.Id}' verification.command 이 알려진 빌드/테스트 러너로 시작하지 않습니다 ('{first}'). " +
                        "정상 러너(dotnet/npm/pytest 등)로 바꾸거나, 본 명령이 의도된 검증인지 확인하세요.");
                }
            }
        }

        // 7.6. task.model이 지정되어 있으면 허용 값(opus|sonnet)인지 확인.
        //      잘못된 값(haiku, gpt-4 등)이 들어오면 ClaudeService 실행 시 알 수 없는 모델로
        //      넘어가 fail할 수 있으니 plan 단계에서 차단.
        foreach (var task in tasks)
        {
            if (string.IsNullOrEmpty(task.Model)) continue;
            if (!ModelResolver.Allowed.Contains(task.Model, StringComparer.OrdinalIgnoreCase))
            {
                report.Errors.Add(
                    $"'{task.Id}'의 model 값 '{task.Model}'이 허용되지 않습니다. " +
                    $"허용: {string.Join(" | ", ModelResolver.Allowed)}");
            }
        }

        // 8. 민감 파일이 modifiedFiles/outputFiles에 명시되어 있으면 error
        foreach (var task in tasks)
        {
            var files = new List<string>();
            if (task.ModifiedFiles is { Count: > 0 }) files.AddRange(task.ModifiedFiles);
            if (task.OutputFiles is { Count: > 0 }) files.AddRange(task.OutputFiles);
            foreach (var f in files)
            {
                if (SensitivePatterns.Any(p =>
                        f.EndsWith(p, StringComparison.OrdinalIgnoreCase)
                        || f.Equals(p, StringComparison.OrdinalIgnoreCase)))
                {
                    report.Errors.Add($"'{task.Id}'가 민감 파일 패턴 '{f}'을 modifiedFiles/outputFiles에 명시했습니다");
                }
            }
        }

        // 9. (개선 A) implementation/testing 카테고리 task은 outputFiles ∪ modifiedFiles ≥ 1.
        //    빈 set이면 (1) file-scoped verification이 무의미하고 (2) pre-rebase cleanup의
        //    `git reset --hard HEAD && git clean -fd` 가 worktree의 모든 변경을 silent
        //    discard 하므로 머지 후 base에서 import 깨짐 → smoke 실패 → auto-rollback.
        //    mighty2 측정에서 batch 한 사이클을 통째 날린 가장 흔한 패턴.
        foreach (var task in tasks)
        {
            if (task.Category != "implementation" && task.Category != "testing") continue;
            var declaredCount = (task.OutputFiles?.Count ?? 0) + (task.ModifiedFiles?.Count ?? 0);
            if (declaredCount == 0)
            {
                report.Errors.Add(
                    $"'{task.Id}' (category={task.Category})는 outputFiles와 modifiedFiles가 모두 비어있습니다. " +
                    "이 task가 만들거나 수정할 파일을 outputFiles에 명시하세요. " +
                    "미선언 파일은 머지 직전 pre-rebase cleanup으로 폐기되어 smoke 실패의 직접 원인이 됩니다.");
            }
        }

        // 10. (개선 B) verification.command이 source 파일 경로를 enumerate한다면 그 파일은
        //     반드시 task의 outputFiles ∪ modifiedFiles ∪ deps의 outputFiles/modifiedFiles에
        //     속해야 한다. 빠진 파일은 worktree에 존재하지 않거나(머지 누락) silent discard
        //     대상이 되어 verification은 통과해도 머지 후 같은 명령이 base에서 깨진다.
        //     실 plan은 항상 category 또는 declared scope를 갖는다 — 둘 다 비어있는 task는
        //     legacy/test 데이터 형태이므로 이 검사에서 제외한다.
        // 중복 ID는 위 1번에서 이미 error로 보고됨. 여기서는 예외를 피하기 위해 첫 항목만 채택.
        var taskById = tasks
            .Where(t => !string.IsNullOrEmpty(t.Id))
            .GroupBy(t => t.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            var cmd = task.Verification?.Command;
            if (string.IsNullOrWhiteSpace(cmd)) continue;
            var hasCategory = !string.IsNullOrWhiteSpace(task.Category);
            var hasDeclared = (task.OutputFiles?.Count ?? 0) + (task.ModifiedFiles?.Count ?? 0) > 0;
            if (!hasCategory && !hasDeclared) continue;

            var declared = BuildDeclaredScope(task, taskById);
            var stripped = StripStringLiterals(cmd);
            var fileTokens = ExtractFileTokens(stripped);
            foreach (var token in fileTokens)
            {
                var norm = NormalizeRepoPath(token);
                if (!declared.Contains(norm))
                {
                    report.Errors.Add(
                        $"'{task.Id}' verification.command이 파일 '{token}'을 참조하는데 " +
                        "이 파일이 task의 outputFiles/modifiedFiles 또는 의존 task의 outputFiles에 없습니다. " +
                        "해당 파일을 outputFiles/modifiedFiles에 추가하거나 verification 명령에서 제거하세요. " +
                        "(미선언 파일은 pre-rebase cleanup으로 폐기되어 머지 후 base에서 사라집니다.)");
                }
            }
        }

        // 11.5. (개선 D) verification.command이 `npx <tool>`을 사용하면 warn.
        //       git worktree에는 node_modules가 없어 npx가 매 task마다 cold install을 유발한다 —
        //       latency 누적, package.json pin 무시(latest 끌어옴), 네트워크 실패에 취약.
        //       해결: `npm test` / `npm run build` 같은 npm script 또는 `node --check tests/X.ts`
        //       같은 install-free 도구로 교체하거나, scaffold task가 package-lock.json을
        //       outputFiles에 선언해 후속 worktree가 base에서 lockfile을 받아 `npm ci`로
        //       결정론적 install을 할 수 있게 만든다.
        foreach (var task in tasks)
        {
            var cmd = task.Verification?.Command;
            if (string.IsNullOrWhiteSpace(cmd)) continue;
            var stripped = StripStringLiterals(cmd);
            var npxMatch = NpxRunnerPattern.Match(stripped);
            if (!npxMatch.Success) continue;
            // 같은 명령 안에 npm ci / npm install / pnpm install / yarn install / bun install이
            // 함께 있으면 self-bootstrap이므로 경고 건너뜀. `test -d node_modules || npm ci; npx ...`
            // 같은 가드 패턴도 자연스럽게 포함된다.
            if (SelfBootstrapInstallPattern.IsMatch(stripped)) continue;

            var tool = npxMatch.Groups[1].Value;
            report.Warnings.Add(
                $"'{task.Id}' verification.command이 `npx {tool}`을 사용합니다 — git worktree에는 " +
                "node_modules가 없어 매 task마다 cold install이 발생하고 package.json의 pinned 버전이 무시됩니다. " +
                "`npm test` / `npm run build` 같은 npm script로 감싸거나, scaffold task의 outputFiles에 " +
                "`package-lock.json`을 추가해 후속 worktree가 base lockfile로 `npm ci`를 수행하게 하세요. " +
                "또는 명령 자체에 `test -d node_modules || npm ci --silent; ...` 같은 self-bootstrap을 추가하세요.");
        }

        // 11. (개선 C) workflow.smokeTest가 specific source files를 enumerate하면 error.
        //     smoke는 모든 batch마다 base 위에서 실행되므로 후속 batch가 만들 파일은 첫 batch
        //     시점에 존재하지 않는다. 파일을 나열하는 명령은 첫 batch부터 false-failure를 만들고
        //     auto-rollback을 유발한다. 전체 트리 명령(pytest -q, npm test, dotnet test 등)으로
        //     교체하거나, 안전한 명령이 없으면 workflow.smokeTest를 생략(ralph 자동 추론).
        var smokeCmd = tm.Data.Workflow?.SmokeTest?.Command;
        if (!string.IsNullOrWhiteSpace(smokeCmd))
        {
            var stripped = StripStringLiterals(smokeCmd!);
            var fileTokens = ExtractFileTokens(stripped);
            if (fileTokens.Count > 0)
            {
                report.Errors.Add(
                    $"workflow.smokeTest.command이 specific source 파일을 enumerate합니다 ({string.Join(", ", fileTokens.Take(5))}" +
                    (fileTokens.Count > 5 ? $" 외 {fileTokens.Count - 5}건" : "") + "). " +
                    "smoke는 batch마다 base에서 실행되므로 후속 batch가 만들 파일은 첫 batch에 아직 없습니다. " +
                    "전체 트리 명령(예: 'pytest -q', 'npm run build && npm test --silent', 'dotnet build && dotnet test', " +
                    "'cargo build && cargo test', 'go build ./... && go test ./...')으로 교체하거나, " +
                    "안전한 명령이 없으면 workflow.smokeTest를 비워두세요(ralph가 stack을 자동 추론합니다).");
            }

            // 11.6. (개선 D) smoke가 `npx <tool>`을 사용하고 self-bootstrap install이 없으면 warn.
            //       `.ralph-smoke` worktree에는 node_modules가 없어 매 batch마다 cold install이
            //       반복되어 240s timeout으로 빠듯하고 npm registry 일시 장애에 취약. `npm ci`로
            //       lockfile 기반 결정론적 install을 명시적으로 추가하도록 권장.
            var smokeNpxMatch = NpxRunnerPattern.Match(stripped);
            if (smokeNpxMatch.Success && !SelfBootstrapInstallPattern.IsMatch(stripped))
            {
                var tool = smokeNpxMatch.Groups[1].Value;
                report.Warnings.Add(
                    $"workflow.smokeTest.command이 `npx {tool}`을 사용합니다 — `.ralph-smoke` worktree에도 " +
                    "node_modules가 없어 매 batch마다 cold install이 반복되며 timeout/네트워크 실패 위험이 누적됩니다. " +
                    "`npm ci --silent && npm run build && npm test --silent` 같은 lockfile 기반 명령으로 교체하고, " +
                    "scaffold task의 outputFiles에 `package-lock.json`을 포함시키세요.");
            }
        }

        // 12. (개선 D 보조) `package.json`을 만드는 task가 lockfile(`package-lock.json`/
        //     `pnpm-lock.yaml`/`yarn.lock`/`bun.lockb`)을 outputFiles에 선언하지 않으면 warn.
        //     scaffold가 npm install을 실행해도 lockfile은 untracked → pre-rebase cleanup이
        //     artifact로 silent discard → base에 lockfile 없음 → 후속 worktree가 결정론적
        //     install을 못함. 명시적 선언으로 lockfile을 base에 영구 반영시켜야 한다.
        foreach (var task in tasks)
        {
            var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddNormalized(declared, task.OutputFiles);
            AddNormalized(declared, task.ModifiedFiles);

            var createsPackageJson = declared.Contains("package.json");
            if (!createsPackageJson) continue;

            var hasLockfile = NodeLockfileNames.Any(declared.Contains);
            if (!hasLockfile)
            {
                report.Warnings.Add(
                    $"'{task.Id}'가 `package.json`을 만들지만 lockfile(package-lock.json/yarn.lock/" +
                    "pnpm-lock.yaml/bun.lockb)을 outputFiles에 선언하지 않았습니다. " +
                    "lockfile이 없으면 후속 worktree가 매번 npm registry에서 latest 버전을 cold install하게 되어 " +
                    "느리고 비결정적입니다. `npm install` 후 생성되는 lockfile을 outputFiles에 명시하세요.");
            }
        }

        return report;
    }

    /// <summary>
    /// task 본인의 outputFiles ∪ modifiedFiles에 deps 체인의 outputFiles ∪ modifiedFiles를 합쳐
    /// "이 task가 verification 시점에 worktree에서 참조 가능한 파일" 의 normalized set을 만든다.
    /// 경로는 forward-slash로 정규화하고 trim해 비교 안정성을 확보한다.
    /// </summary>
    private static HashSet<string> BuildDeclaredScope(
        TaskItem task, IReadOnlyDictionary<string, TaskItem> taskById)
    {
        var scope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddNormalized(scope, task.OutputFiles);
        AddNormalized(scope, task.ModifiedFiles);
        if (task.DependsOn is { Count: > 0 })
        {
            foreach (var depId in task.DependsOn)
            {
                if (!taskById.TryGetValue(depId, out var dep)) continue;
                AddNormalized(scope, dep.OutputFiles);
                AddNormalized(scope, dep.ModifiedFiles);
            }
        }
        return scope;
    }

    private static void AddNormalized(HashSet<string> set, IList<string>? files)
    {
        if (files is not { Count: > 0 }) return;
        foreach (var f in files)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            set.Add(NormalizeRepoPath(f));
        }
    }

    private static string NormalizeRepoPath(string path)
        => path.Replace('\\', '/').Trim().TrimStart('/');

    /// <summary>
    /// shell 명령에서 source 파일로 보이는 토큰을 추출한다. flag(`-x`/`--y`),
    /// glob(`*`/`?`), 디렉터리(`tests/`), 셸 메타문자만 있는 토큰은 제외한다.
    /// 입력은 string-literal이 이미 stripped된 상태여야 한다 (false positive 방지).
    /// </summary>
    private static IReadOnlyList<string> ExtractFileTokens(string strippedCmd)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(strippedCmd)) return result;

        var separators = new[] { ' ', '\t', '\n', '\r' };
        foreach (var raw in strippedCmd.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            // 양 끝 셸 메타/구두점 제거 — `cmd1;` `(cmd)` 같은 형태에서 토큰만 남긴다.
            var tok = raw.Trim().Trim(';', '&', '|', '(', ')', '"', '\'', '`', ',', ':');
            if (tok.Length == 0) continue;
            if (tok.StartsWith("-")) continue;            // flag
            if (tok.Contains('*') || tok.Contains('?')) continue; // glob
            if (tok.EndsWith("/") || tok.EndsWith("\\")) continue; // directory
            if (tok.Contains('=')) continue;              // env assignment / kw arg
            if (tok.StartsWith("$") || tok.StartsWith("`")) continue; // env / cmd-sub residue

            // 확장자 검사 — Path.GetExtension은 path가 dot으로 시작해도 안전하게 동작.
            var ext = Path.GetExtension(tok);
            if (string.IsNullOrEmpty(ext)) continue;
            if (!SourceFileExtensions.Contains(ext)) continue;

            result.Add(tok);
        }
        return result;
    }

    /// <summary>
    /// 인터프리터 inline script body에 statement separator로 사용된 `\n`/`\t`/`\r`이
    /// 있는지 확인합니다. shell의 single/double quote는 `\n`을 LF로 변환하지 않으므로
    /// top-level에 있는 backslash-n은 거의 항상 SyntaxError를 일으킵니다.
    ///
    /// 단, 인터프리터의 string literal 내부(`"..."`, `'...'`, `` `...` ``)에 들어간 `\n`은
    /// 해당 언어가 자체 escape rule로 처리하므로 안전 → false positive 방지를 위해 건너뜁니다.
    /// `\\n`(escaped backslash) 도 단일 backslash로 전달되므로 안전.
    /// </summary>
    private static bool ContainsBadEscape(string body)
    {
        char? inString = null;
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];

            if (inString.HasValue)
            {
                // string literal 내부 — 어떤 escape도 안전 (인터프리터가 처리)
                if (c == '\\' && i + 1 < body.Length) { i++; continue; }
                if (c == inString.Value) inString = null;
                continue;
            }

            if (c == '"' || c == '\'' || c == '`')
            {
                inString = c;
                continue;
            }

            if (c == '\\' && i + 1 < body.Length)
            {
                var next = body[i + 1];
                if (next == '\\') { i++; continue; }     // \\ → 단일 backslash, 안전
                if (next == 'n' || next == 't' || next == 'r') return true;
            }
        }
        return false;
    }

    /// <summary>
    /// `'...'` / `"..."` string literal 영역의 모든 문자를 공백으로 치환한 사본을 반환합니다.
    /// 정규식 매칭의 false positive 를 줄이기 위한 전처리. backtick(``` ` ```)은 명령 치환 자체가
    /// 위험 패턴이므로 보존합니다. `\`-escape 는 string literal 내부에서만 다음 문자를 같이 비웁니다.
    /// </summary>
    private static string StripStringLiterals(string body)
    {
        var sb = new StringBuilder(body.Length);
        char? inStr = null;
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (inStr.HasValue)
            {
                if (c == '\\' && i + 1 < body.Length)
                {
                    sb.Append("  ");
                    i++;
                    continue;
                }
                if (c == inStr.Value)
                {
                    inStr = null;
                    sb.Append(' ');
                    continue;
                }
                sb.Append(c == '\n' ? '\n' : ' ');
                continue;
            }
            if (c == '"' || c == '\'')
            {
                inStr = c;
                sb.Append(' ');
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// string-literal-stripped 명령에서 화이트리스트 러너 토큰이 어디든 등장하는지 확인합니다.
    /// guard 체인(`test -d node_modules || npm ci; npx tsc ...`)에서 실제 러너가 후속 statement에
    /// 있는 경우 [info] false positive를 막기 위해 사용합니다.
    /// </summary>
    private static bool ContainsAnyWhitelistedTool(string strippedCmd)
    {
        if (string.IsNullOrWhiteSpace(strippedCmd)) return false;
        var separators = new[] { ' ', '\t', '\n', '\r', ';', '|', '&', '(', ')' };
        foreach (var raw in strippedCmd.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var tok = raw.Trim();
            if (tok.Length == 0) continue;
            if (WhitelistedTools.Contains(tok, StringComparer.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 명령의 첫 실행 토큰(공백 전까지)을 추출합니다. `FOO=bar dotnet test` 같은 환경 변수
    /// prefix 는 건너뛰고 실제 명령을 반환합니다.
    /// </summary>
    private static string? ExtractFirstToken(string cmd)
    {
        var trimmed = cmd.TrimStart();
        if (trimmed.Length == 0) return null;

        var end = 0;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end])) end++;
        if (end == 0) return null;

        var token = trimmed[..end];
        // 환경 변수 prefix (FOO=bar) 는 skip — 단, `./script` 는 토큰 그대로 반환.
        if (!token.StartsWith("./") && token.Contains('='))
        {
            var rest = trimmed[end..];
            return string.IsNullOrWhiteSpace(rest) ? null : ExtractFirstToken(rest);
        }
        return token;
    }

    /// <summary>
    /// top-level statement separator (`;`, `&&`, `||`, LF) 의 개수 + 1 을 반환합니다.
    /// `dotnet test 2>&1 | tee out.log` 같은 single-pipe 는 separator 가 아니므로 카운트하지 않습니다.
    /// </summary>
    private static int CountStatements(string topLevelOnly)
    {
        var count = 1;
        for (var i = 0; i < topLevelOnly.Length; i++)
        {
            var c = topLevelOnly[i];
            if (c == ';' || c == '\n')
            {
                count++;
                continue;
            }
            if (i + 1 < topLevelOnly.Length)
            {
                if ((c == '&' && topLevelOnly[i + 1] == '&')
                    || (c == '|' && topLevelOnly[i + 1] == '|'))
                {
                    count++;
                    i++;
                }
            }
        }
        return count;
    }

    /// <summary>
    /// from에서 to로 의존 경로가 있는지 (from이 to에 의존하는지) BFS로 확인합니다.
    /// </summary>
    private static bool HasDependencyPath(IReadOnlyList<TaskItem> tasks, string from, string to)
    {
        var byId = tasks.ToDictionary(t => t.Id, t => t);
        var visited = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current)) continue;
            if (!byId.TryGetValue(current, out var task)) continue;
            if (task.DependsOn is not { Count: > 0 }) continue;
            foreach (var dep in task.DependsOn)
            {
                if (dep == to) return true;
                queue.Enqueue(dep);
            }
        }
        return false;
    }

    /// <summary>
    /// 검증 결과를 콘솔에 출력합니다. errors가 있으면 1을 반환.
    /// </summary>
    public static int PrintReport(PlanValidationReport report, bool failOnWarning = false)
    {
        if (report.IsClean)
        {
            AnsiConsole.MarkupLine("[green]✓ Plan validation passed (errors: 0, warnings: 0).[/]");
            return 0;
        }

        if (report.HasErrors)
        {
            AnsiConsole.MarkupLine($"\n[red]✗ Errors ({report.Errors.Count}):[/]");
            foreach (var e in report.Errors)
                AnsiConsole.MarkupLine($"  [red]•[/] {Markup.Escape(e)}");
        }

        if (report.HasWarnings)
        {
            AnsiConsole.MarkupLine($"\n[yellow]⚠ Warnings ({report.Warnings.Count}):[/]");
            foreach (var w in report.Warnings)
                AnsiConsole.MarkupLine($"  [yellow]•[/] {Markup.Escape(w)}");
        }

        AnsiConsole.WriteLine();
        return (report.HasErrors || (failOnWarning && report.HasWarnings)) ? 1 : 0;
    }
}
