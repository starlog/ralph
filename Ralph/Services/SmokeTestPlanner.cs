using System.Text.Json;
using Ralph.Models;

namespace Ralph.Services;

/// <summary>
/// post-merge smoke test의 우선순위/추론 로직 모음. 순수 함수이며 외부 상태에 의존하지 않는다 —
/// repoRoot 안의 파일을 읽고 분석할 뿐, 명령은 실행하지 않는다.
///
/// 우선순위 (Plan):
///   1. noSmokeTest=true                 → null (스킵)
///   2. cliCommand (--smoke-test)        → 그대로 사용 (수동 1회용)
///   3. envCommand (RALPH_SMOKE_TEST_COMMAND) → 그대로 사용
///   4. configured (workflow.smokeTest)  → 그대로 사용
///   5. auto-infer from repoRoot         → 다중 marker `&&` 결합 가능
///
/// 추론 단계는 변경 파일이 모두 docs/markdown만이면 스킵한다 (changedFiles 인자 제공 시).
/// 명시적으로 지정된(2-4) 경로는 docs-only로도 스킵하지 않는다 — 사용자 의도 존중.
/// </summary>
internal static class SmokeTestPlanner
{
    /// <summary>1회용 CLI/env 명령에 사용할 기본 timeout(초).</summary>
    private const int DefaultManualTimeoutSec = 180;

    /// <summary>
    /// 우선순위에 따라 실행할 smoke test spec을 결정한다. null이면 스킵.
    /// changedFiles는 last-batch에서 base 브랜치에 새로 들어간 파일 목록 (선택). docs만이면 추론 단계 스킵.
    /// </summary>
    public static VerificationSpec? Plan(
        string repoRoot,
        VerificationSpec? configured,
        string? cliCommand,
        string? envCommand,
        bool noSmokeTest,
        IReadOnlyList<string>? changedFiles = null)
    {
        if (noSmokeTest) return null;

        if (!string.IsNullOrWhiteSpace(cliCommand))
            return new VerificationSpec { Command = cliCommand!.Trim(), TimeoutSec = DefaultManualTimeoutSec };

        if (!string.IsNullOrWhiteSpace(envCommand))
            return new VerificationSpec { Command = envCommand!.Trim(), TimeoutSec = DefaultManualTimeoutSec };

        if (configured is not null && !string.IsNullOrWhiteSpace(configured.Command))
            return configured;

        // 추론 단계만 docs-only 스킵을 적용한다 — 명시 설정은 사용자가 의도적으로 켠 것이므로 항상 실행.
        if (changedFiles is not null && AllChangesAreDocs(changedFiles))
            return null;

        return InferFromMarkers(repoRoot);
    }

    /// <summary>
    /// repoRoot 마커만 보고 추론. Plan의 5단계와 동일하지만 changedFiles/configured 없이 단독 호출 가능.
    /// 기존 ParallelExecutor.InferSmokeTestCommand를 대체.
    /// </summary>
    public static VerificationSpec? Infer(string repoRoot) => InferFromMarkers(repoRoot);

    /// <summary>
    /// fix2 #7: smoke 실패 시 자동 revert 커밋 메시지에 첨부할 stdout/stderr 꼬리.
    /// UTF-8 바이트 기준 maxBytes를 넘으면 끝에서 잘라 truncated 표시를 prefix한다.
    /// 멀티바이트 문자가 깨지지 않도록 char 단위로 진입한다.
    /// </summary>
    public static string TruncateTail(string? text, int maxBytes = 4000)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var totalBytes = System.Text.Encoding.UTF8.GetByteCount(text);
        if (totalBytes <= maxBytes) return text;

        var charCount = Math.Min(text.Length, maxBytes);
        while (charCount > 0
               && System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(text.Length - charCount)) > maxBytes)
        {
            charCount--;
        }
        var tail = text.Substring(text.Length - charCount);
        return $"... (잘림, 원본 {totalBytes} bytes)\n{tail}";
    }

    private static bool AllChangesAreDocs(IReadOnlyList<string> changedFiles)
    {
        if (changedFiles.Count == 0) return false;
        foreach (var raw in changedFiles)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var p = raw.Replace('\\', '/').Trim().ToLowerInvariant();
            if (p.Length == 0) continue;
            var ext = Path.GetExtension(p);
            var fileName = Path.GetFileName(p);
            var isDocFile = ext is ".md" or ".markdown" or ".rst" or ".txt" or ".adoc";
            var isDocDir = p.StartsWith("docs/") || p.StartsWith("doc/");
            var isMeta = fileName is "license" or "readme" or ".gitignore" or "changelog";
            if (!(isDocFile || isDocDir || isMeta)) return false;
        }
        return true;
    }

    private static VerificationSpec? InferFromMarkers(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return null;

        var parts = new List<(string Command, int TimeoutSec)>();

        bool HasTopLevel(string pattern) =>
            Directory.EnumerateFiles(repoRoot, pattern, SearchOption.TopDirectoryOnly).Any();

        // .NET — *.csproj/*.sln는 그대로 dotnet build (검증 비용 적정).
        if (HasTopLevel("*.csproj") || HasTopLevel("*.sln"))
            parts.Add(("dotnet build -nologo", 180));

        // Node 생태계 — package.json scripts/lockfile/workspace 인식.
        if (HasTopLevel("package.json"))
        {
            var node = InferNodeCommand(repoRoot);
            if (node is { } n) parts.Add(n);
        }

        // Cargo — build보다 빠르고 type/borrow check은 동일.
        if (HasTopLevel("Cargo.toml"))
            parts.Add(("cargo check --quiet", 180));

        // Go
        if (HasTopLevel("go.mod"))
            parts.Add(("go build ./...", 180));

        // Python — bytecode 컴파일은 syntax/import 오류 잡는 가벼운 검증.
        // Windows에서는 `python3.exe`가 Microsoft Store 스텁(exit 9009)일 확률이 높아 `python`을 사용.
        if (HasTopLevel("pyproject.toml") || HasTopLevel("setup.py") || HasTopLevel("requirements.txt"))
            parts.Add(($"{HostPlatform.PythonCommand} -m compileall -q .", 120));

        if (parts.Count == 0) return null;

        var combined = string.Join(" && ", parts.Select(p => p.Command));
        var maxTimeout = parts.Max(p => p.TimeoutSec);
        return new VerificationSpec { Command = combined, TimeoutSec = maxTimeout };
    }

    /// <summary>
    /// package.json 내용을 보고 Node 빌드/테스트 검증 명령을 결정.
    /// per-task verification은 compile/typecheck만 수행하고 실제 test 실행은 smoke가 담당하므로
    /// (PlanGenerator Rule 13/14), build와 test script가 모두 있으면 chain하여 두 단계를 한 번에 검증한다.
    ///   - scripts.build + scripts.test → `<pm> [-r] run build && <pm> test --silent` (test 실행이 smoke의 본업)
    ///   - scripts.build만               → `<pm> [-r] run build`
    ///   - scripts.test만                → `<pm> test --silent`
    ///   - tsconfig.json만               → `npx --no-install tsc --noEmit`
    ///   - 모두 없음                     → null (스킵; npm test 강제 실행 회피)
    /// </summary>
    private static (string Command, int TimeoutSec)? InferNodeCommand(string repoRoot)
    {
        var pkgPath = Path.Combine(repoRoot, "package.json");
        if (!File.Exists(pkgPath)) return null;

        var pm = DetectPackageManager(repoRoot);
        var info = ReadPackageJson(pkgPath);
        var isMonorepo = info.HasWorkspaces
            || File.Exists(Path.Combine(repoRoot, "pnpm-workspace.yaml"))
            || File.Exists(Path.Combine(repoRoot, "turbo.json"));

        // pnpm/yarn(berry/classic 모두) -r 지원. npm/bun은 monorepo도 단일 build script만 실행.
        var recursive = isMonorepo && pm is "pnpm" or "yarn";
        var buildCmd = recursive ? $"{pm} -r run build" : $"{pm} run build";
        var testCmd = $"{pm} test --silent";

        if (info.HasBuildScript && info.HasTestScript)
            return ($"{buildCmd} && {testCmd}", 480);

        if (info.HasBuildScript)
            return (buildCmd, 300);

        if (File.Exists(Path.Combine(repoRoot, "tsconfig.json")))
            return ("npx --no-install tsc --noEmit", 180);

        if (info.HasTestScript)
            return (testCmd, 180);

        return null;
    }

    private static string DetectPackageManager(string repoRoot)
    {
        if (File.Exists(Path.Combine(repoRoot, "pnpm-lock.yaml"))) return "pnpm";
        if (File.Exists(Path.Combine(repoRoot, "yarn.lock"))) return "yarn";
        if (File.Exists(Path.Combine(repoRoot, "bun.lockb"))) return "bun";
        if (File.Exists(Path.Combine(repoRoot, "bun.lock"))) return "bun";
        return "npm";
    }

    private readonly record struct PackageJsonInfo(bool HasBuildScript, bool HasTestScript, bool HasWorkspaces);

    private static PackageJsonInfo ReadPackageJson(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new PackageJsonInfo(false, false, false);

            bool hasBuild = false, hasTest = false, hasWs = false;

            if (root.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object)
            {
                hasBuild = HasNonEmptyString(scripts, "build");
                hasTest = HasNonEmptyString(scripts, "test");
            }

            if (root.TryGetProperty("workspaces", out var ws))
            {
                // npm/yarn classic은 array, yarn berry는 object {packages:[...]}, 둘 다 monorepo signal.
                hasWs = ws.ValueKind == JsonValueKind.Array || ws.ValueKind == JsonValueKind.Object;
            }

            return new PackageJsonInfo(hasBuild, hasTest, hasWs);
        }
        catch
        {
            // 파싱 실패는 invalid package.json — fail-safe로 모든 플래그 false.
            return new PackageJsonInfo(false, false, false);
        }
    }

    private static bool HasNonEmptyString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(v.GetString());
}
