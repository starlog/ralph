using System.Text.RegularExpressions;
using Xunit;

namespace Ralph.Tests;

/// <summary>
/// fix5 회귀 방지: silent error swallowing 패턴(logger?., 빈 typed catch, null prompt 마스킹)이
/// Services/Commands 코드베이스에 재도입되지 않음을 정적 텍스트 분석으로 검증.
/// </summary>
public class StaticHygieneTests
{
    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Ralph", "Services")))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException("Ralph 소스 루트를 찾을 수 없습니다 — AppContext.BaseDirectory: " + AppContext.BaseDirectory);
        return dir.FullName;
    }

    private static IEnumerable<(string File, string Content)> GetSourceFiles(params string[] subDirs)
    {
        var root = FindSourceRoot();
        foreach (var sub in subDirs)
        {
            var dir = Path.Combine(root, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "*.cs", SearchOption.TopDirectoryOnly))
                yield return (f, File.ReadAllText(f));
        }
    }

    [Fact]
    public void No_nullable_logger_calls_in_services_and_commands()
    {
        var violations = new List<string>();
        foreach (var (file, content) in GetSourceFiles("Ralph/Services", "Ralph/Commands"))
        {
            var lines = content.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("logger?."))
                    violations.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(violations.Count == 0,
            $"logger?. 패턴 {violations.Count}건 발견 (fix5 회귀):\n" + string.Join("\n", violations));
    }

    [Fact]
    public void No_empty_typed_catch_in_services_and_commands()
    {
        // catch (SomeType ex) { } — 타입이 명시된 완전히 빈 catch (주석 없음)
        var pattern = new Regex(@"catch\s*\(\s*\w[^)]*\)\s*\{\s*\}", RegexOptions.Singleline);
        var violations = new List<string>();
        foreach (var (file, content) in GetSourceFiles("Ralph/Services", "Ralph/Commands"))
        {
            var lines = content.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (pattern.IsMatch(lines[i]))
                    violations.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(violations.Count == 0,
            $"빈 typed catch 블록 {violations.Count}건 발견 (fix5 회귀):\n" + string.Join("\n", violations));
    }

    [Fact]
    public void PromptBuilder_null_prompt_is_not_masked_with_placeholder()
    {
        var root = FindSourceRoot();
        var path = Path.Combine(root, "Ralph", "Services", "PromptBuilder.cs");
        Assert.True(File.Exists(path), $"파일 없음: {path}");
        var content = File.ReadAllText(path);
        // fix5: "(prompt 미지정)" 플레이스홀더 마스킹 제거 — 이제 InvalidOperationException을 던져야 함
        Assert.DoesNotContain("prompt 미지정", content);
        Assert.Contains("InvalidOperationException", content);
    }

    [Fact]
    public void WorktreeService_worktreeBase_delete_catch_logs_warning()
    {
        var root = FindSourceRoot();
        var path = Path.Combine(root, "Ralph", "Services", "WorktreeService.cs");
        Assert.True(File.Exists(path), $"파일 없음: {path}");
        var content = File.ReadAllText(path);
        // fix5: worktree 베이스 디렉터리 삭제 catch가 logger.Warn을 호출해야 함
        var idx = content.IndexOf("Directory.Delete(_worktreeBase", StringComparison.Ordinal);
        Assert.True(idx >= 0, "Directory.Delete(_worktreeBase 패턴 없음 — 리팩터링으로 경로가 바뀌었을 수 있음");
        var window = content.Substring(idx, Math.Min(400, content.Length - idx));
        Assert.Contains("logger.Warn", window);
    }
}
