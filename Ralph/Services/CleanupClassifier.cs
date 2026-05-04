namespace Ralph.Services;

/// <summary>
/// pre-rebase cleanup이 폐기하려는 파일을 분류한다.
/// 목적: 빌드 부산물(node_modules/coverage 등)은 조용히 버려도 안전하지만,
/// 실제 소스 파일이 undeclared로 남아있다면 plan에서 outputFiles 누락된
/// 진짜 코드일 가능성이 높다 — 이 경우 사용자에게 시각적으로 알리거나
/// (--strict-cleanup 옵션 시) 실행을 중단해야 한다.
/// </summary>
public static class CleanupClassifier
{
    /// <summary>
    /// 항상 조용히 폐기 — 명백한 빌드/캐시 부산물. 디렉터리 segment 또는
    /// 파일명 suffix로 매칭한다.
    /// </summary>
    private static readonly string[] ArtifactDirSegments =
    [
        "node_modules/", "dist/", "bin/", "obj/", "target/", "build/",
        ".next/", ".nuxt/", ".svelte-kit/",
        "__pycache__/", ".pytest_cache/", ".mypy_cache/", ".ruff_cache/",
        "coverage/", ".coverage/", ".nyc_output/",
        ".vitest/", ".cache/", ".parcel-cache/", ".turbo/",
        ".gradle/", ".idea/", ".vs/",
    ];

    /// <summary>파일명/확장자 기반 artifact (lockfile 포함 — 의도된 변경이라면 declared여야 함).</summary>
    private static readonly string[] ArtifactSuffixes =
    [
        ".tsbuildinfo", ".pyc", ".pyo", ".class", ".o", ".obj",
        ".log", ".tmp",
    ];

    private static readonly string[] LockfileNames =
    [
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml",
        "Cargo.lock", "Gemfile.lock", "poetry.lock", "uv.lock",
        "composer.lock", "go.sum",
    ];

    /// <summary>
    /// 소스 코드로 보이는 파일. 이게 undeclared로 남아있으면 plan 결함 신호.
    /// </summary>
    private static readonly string[] SourceSuffixes =
    [
        // JS/TS
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".vue", ".svelte",
        // Python
        ".py", ".pyi",
        // Go / Rust / C-family
        ".go", ".rs", ".c", ".cc", ".cpp", ".cxx", ".h", ".hpp", ".hh",
        // .NET / JVM
        ".cs", ".fs", ".vb", ".java", ".kt", ".kts", ".scala", ".groovy",
        // Other languages
        ".rb", ".swift", ".m", ".mm", ".php", ".lua", ".dart", ".ex", ".exs",
        ".clj", ".cljs", ".hs", ".ml", ".pl", ".r", ".jl",
        // Web assets that are usually authored
        ".css", ".scss", ".sass", ".less", ".html", ".htm",
        // Schema / DSL
        ".sql", ".proto", ".graphql", ".tf",
    ];

    public sealed record CleanupReport(
        IReadOnlyList<string> SourceFiles,
        IReadOnlyList<string> Artifacts,
        IReadOnlyList<string> Other);

    /// <summary>
    /// `git status --porcelain` 출력을 라인별로 분류한다. 입력이 비어 있으면
    /// 모든 리스트가 빈 보고서를 반환.
    /// </summary>
    public static CleanupReport Classify(string statusPorcelain)
    {
        var sources = new List<string>();
        var artifacts = new List<string>();
        var other = new List<string>();

        if (string.IsNullOrWhiteSpace(statusPorcelain))
            return new CleanupReport(sources, artifacts, other);

        foreach (var rawLine in statusPorcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // porcelain v1: "XY path" — XY는 2글자 status 코드, 그 다음 공백, 그 다음 경로.
            // rename 케이스(R)는 "R  old -> new" 형식이지만 cleanup 관점에선 new 경로가 중요.
            var line = rawLine.TrimEnd('\r');
            if (line.Length < 3) { other.Add(line); continue; }
            var path = line.Substring(3).Trim();
            if (path.Contains(" -> "))
                path = path.Substring(path.IndexOf(" -> ", StringComparison.Ordinal) + 4);

            var category = ClassifyPath(path);
            switch (category)
            {
                case Category.Source: sources.Add(path); break;
                case Category.Artifact: artifacts.Add(path); break;
                default: other.Add(path); break;
            }
        }

        return new CleanupReport(sources, artifacts, other);
    }

    private enum Category { Source, Artifact, Other }

    private static Category ClassifyPath(string path)
    {
        // 디렉터리 segment 체크 — node_modules/foo.ts도 artifact (소스 같지만 vendor)
        var normalized = path.Replace('\\', '/');
        foreach (var seg in ArtifactDirSegments)
        {
            if (normalized.Contains(seg, StringComparison.OrdinalIgnoreCase))
                return Category.Artifact;
        }

        var fileName = Path.GetFileName(normalized);
        foreach (var lock_ in LockfileNames)
        {
            if (string.Equals(fileName, lock_, StringComparison.OrdinalIgnoreCase))
                return Category.Artifact;
        }

        foreach (var suf in ArtifactSuffixes)
        {
            if (normalized.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
                return Category.Artifact;
        }

        foreach (var suf in SourceSuffixes)
        {
            if (normalized.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
                return Category.Source;
        }

        return Category.Other;
    }
}
