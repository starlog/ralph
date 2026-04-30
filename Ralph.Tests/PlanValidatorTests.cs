using Ralph.Models;
using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

public class PlanValidatorTests
{
    private static async Task<TaskManager> Tm(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ralph-validator-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        return await TaskManager.LoadAsync(path);
    }

    [Fact]
    public async Task Clean_plan_has_no_errors_or_warnings()
    {
        var tm = await Tm("""
        {
          "tasks": [
            {"id":"a","title":"A","done":false,"prompt":"implement A"},
            {"id":"b","title":"B","done":false,"prompt":"implement B","dependsOn":["a"]}
          ]
        }
        """);
        var r = PlanValidator.Validate(tm);
        Assert.False(r.HasErrors, $"errors: {string.Join("; ", r.Errors)}");
        Assert.False(r.HasWarnings, $"warnings: {string.Join("; ", r.Warnings)}");
    }

    [Fact]
    public async Task Duplicate_id_is_error()
    {
        var tm = await Tm("""
        {"tasks":[
          {"id":"x","title":"X1","done":false,"prompt":"p"},
          {"id":"x","title":"X2","done":false,"prompt":"p"}
        ]}
        """);
        var r = PlanValidator.Validate(tm);
        Assert.True(r.HasErrors);
        Assert.Contains(r.Errors, e => e.Contains("중복") && e.Contains("'x'"));
    }

    [Fact]
    public async Task Cycle_is_error()
    {
        var tm = await Tm("""
        {"tasks":[
          {"id":"a","title":"A","done":false,"prompt":"p","dependsOn":["b"]},
          {"id":"b","title":"B","done":false,"prompt":"p","dependsOn":["a"]}
        ]}
        """);
        var r = PlanValidator.Validate(tm);
        Assert.True(r.HasErrors);
        Assert.Contains(r.Errors, e => e.Contains("순환"));
    }

    [Fact]
    public async Task Self_dependency_is_error()
    {
        var tm = await Tm("""
        {"tasks":[
          {"id":"x","title":"X","done":false,"prompt":"p","dependsOn":["x"]}
        ]}
        """);
        var r = PlanValidator.Validate(tm);
        Assert.True(r.HasErrors);
        Assert.Contains(r.Errors, e => e.Contains("자기 자신"));
    }

    [Fact]
    public async Task Unknown_dependency_id_is_error()
    {
        var tm = await Tm("""
        {"tasks":[
          {"id":"a","title":"A","done":false,"prompt":"p","dependsOn":["nonexistent"]}
        ]}
        """);
        var r = PlanValidator.Validate(tm);
        Assert.True(r.HasErrors);
        Assert.Contains(r.Errors, e => e.Contains("존재하지 않는") && e.Contains("nonexistent"));
    }

    [Fact]
    public async Task Empty_prompt_is_error()
    {
        // fix5: 빈 prompt는 silent 마스킹을 유발하므로 plan 단계에서 error로 차단한다.
        var tm = await Tm("""
        {"tasks":[{"id":"a","title":"A","done":false}]}
        """);
        var r = PlanValidator.Validate(tm);
        Assert.True(r.HasErrors);
        Assert.Contains(r.Errors, e => e.Contains("prompt가 비어"));
    }

    [Fact]
    public async Task Sensitive_file_in_modifiedFiles_is_error()
    {
        var tm = await Tm("""
        {"tasks":[{
          "id":"a","title":"A","done":false,"prompt":"p",
          "modifiedFiles":["config.env",".env","secrets/credentials.json"]
        }]}
        """);
        var r = PlanValidator.Validate(tm);
        Assert.True(r.HasErrors);
        Assert.True(r.Errors.Count(e => e.Contains("민감")) >= 2);
    }

    [Fact]
    public async Task Overlapping_files_without_dependency_is_warning()
    {
        var tm = await Tm("""
        {"tasks":[
          {"id":"a","title":"A","done":false,"prompt":"p","modifiedFiles":["src/shared.ts"]},
          {"id":"b","title":"B","done":false,"prompt":"p","modifiedFiles":["src/shared.ts"]}
        ]}
        """);
        var r = PlanValidator.Validate(tm);
        Assert.False(r.HasErrors);
        Assert.True(r.HasWarnings);
        Assert.Contains(r.Warnings, w => w.Contains("머지 충돌 위험") && w.Contains("shared.ts"));
    }

    [Fact]
    public async Task Overlapping_files_with_dependency_is_ok()
    {
        // a → b 의존 관계가 있으면 같은 파일 수정해도 순서가 보장됨 → 경고 없음
        var tm = await Tm("""
        {"tasks":[
          {"id":"a","title":"A","done":false,"prompt":"p","modifiedFiles":["src/shared.ts"]},
          {"id":"b","title":"B","done":false,"prompt":"p","dependsOn":["a"],"modifiedFiles":["src/shared.ts"]}
        ]}
        """);
        var r = PlanValidator.Validate(tm);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("머지 충돌 위험"));
    }

    [Fact]
    public async Task Testing_category_without_test_keyword_is_warning()
    {
        var tm = await Tm("""
        {"tasks":[
          {"id":"x-test","title":"Test","done":false,"category":"testing","prompt":"do something else"}
        ]}
        """);
        var r = PlanValidator.Validate(tm);
        Assert.Contains(r.Warnings, w => w.Contains("test/테스트/검증"));
    }

    [Fact]
    public async Task Commit_category_with_keyword_no_warning()
    {
        var tm = await Tm("""
        {"tasks":[
          {"id":"x-commit","title":"Commit","done":false,"category":"commit","prompt":"git commit changes"}
        ]}
        """);
        var r = PlanValidator.Validate(tm);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("commit/커밋"));
    }

    [Theory]
    [InlineData("python3 -c \"from m import f\\nimport sys\\nprint(f(1,2))\"")]
    [InlineData("python -c \"from m import f\\nprint('ok')\"")]
    [InlineData("node -e \"const m = require('./m')\\nconsole.log(m.f(1,2))\"")]
    [InlineData("nodejs --eval \"const m=require('./m')\\nconsole.log(m.f(1))\"")]
    [InlineData("node -p \"const m=require('./m')\\nm.f(1)\"")]
    [InlineData("bun -e \"import m from './m'\\nconsole.log(m)\"")]
    [InlineData("ruby -e \"require 'm'\\nputs M.f(1,2)\"")]
    [InlineData("perl -e \"use M;\\nprint M::f(1)\"")]
    [InlineData("php -r \"require 'm.php';\\necho m::f(1);\"")]
    [InlineData("lua -e \"require 'm'\\nprint(m.f(1))\"")]
    [InlineData("python3 -c 'from m import f\\nprint(f(1))'")] // single-quoted form — same problem
    public async Task Verification_inline_script_with_newline_escape_is_error(string command)
    {
        var json = $$"""
        {"tasks":[{
          "id":"x","title":"X","done":false,"prompt":"p",
          "verification":{"command":{{System.Text.Json.JsonSerializer.Serialize(command)}}}
        }]}
        """;
        var tm = await Tm(json);
        var r = PlanValidator.Validate(tm);
        Assert.True(r.HasErrors, $"expected error for command: {command}\nactual errors: {string.Join("; ", r.Errors)}");
        Assert.Contains(r.Errors, e => e.Contains("verification.command") && e.Contains("이스케이프"));
    }

    [Theory]
    [InlineData("pytest -q tests/")]
    [InlineData("dotnet test")]
    [InlineData("go test ./...")]
    [InlineData("npm test --silent")]
    [InlineData("cargo test --quiet")]
    [InlineData("tsc --noEmit")]
    [InlineData("python3 -c \"from m import f; assert f(10,3)==3.5; print('OK')\"")]
    [InlineData("node -e \"const m=require('./m'); console.log(m.f(1,2))\"")]
    [InlineData("python3 path/to/check.py")]
    [InlineData("ruby -e 'require \"m\"; puts M.f(1)'")]
    // \n inside a string literal is the interpreter's own escape — valid:
    [InlineData("python3 -c \"print('hello\\nworld')\"")]
    [InlineData("node -e \"console.log('a\\nb')\"")]
    [InlineData("ruby -e 'puts \"line1\\nline2\"'")]
    public async Task Verification_safe_command_is_clean(string command)
    {
        var json = $$"""
        {"tasks":[{
          "id":"x","title":"X","done":false,"prompt":"p",
          "verification":{"command":{{System.Text.Json.JsonSerializer.Serialize(command)}}}
        }]}
        """;
        var tm = await Tm(json);
        var r = PlanValidator.Validate(tm);
        Assert.DoesNotContain(r.Errors, e => e.Contains("verification.command"));
    }

    [Fact]
    public async Task Verification_ansi_c_quoting_dollar_quote_is_clean()
    {
        // bash ANSI-C quoting $'...' 은 \n을 실제 LF로 확장 → 안전
        var json = """
        {"tasks":[{
          "id":"x","title":"X","done":false,"prompt":"p",
          "verification":{"command":"python3 -c $'from m import f\nprint(f(1,2))'"}
        }]}
        """;
        var tm = await Tm(json);
        var r = PlanValidator.Validate(tm);
        Assert.DoesNotContain(r.Errors, e => e.Contains("verification.command"));
    }

    [Theory]
    [InlineData("set -e\\ncd /tmp\\necho hello")]
    [InlineData("out=$(python3 main.py '3+2')\\n[ \"$out\" = \"5\" ] || exit 1\\necho OK")]
    [InlineData("cmd1\\ncmd2 && cmd3")]
    [InlineData("echo a\\techo b")]
    public async Task Verification_shell_level_multiline_escape_is_error(string command)
    {
        var json = $$"""
        {"tasks":[{
          "id":"x","title":"X","done":false,"prompt":"p",
          "verification":{"command":{{System.Text.Json.JsonSerializer.Serialize(command)}}}
        }]}
        """;
        var tm = await Tm(json);
        var r = PlanValidator.Validate(tm);
        Assert.True(r.HasErrors, $"expected error for command: {command}\nactual errors: {string.Join("; ", r.Errors)}");
        Assert.Contains(r.Errors, e => e.Contains("verification.command") && e.Contains("top-level"));
    }

    [Theory]
    [InlineData("bash scripts/check.sh")]
    [InlineData("cmd1 && cmd2 || cmd3")]
    [InlineData("for f in *.py; do python3 \"$f\"; done")]
    public async Task Verification_normal_shell_command_is_clean(string command)
    {
        var json = $$"""
        {"tasks":[{
          "id":"x","title":"X","done":false,"prompt":"p",
          "verification":{"command":{{System.Text.Json.JsonSerializer.Serialize(command)}}}
        }]}
        """;
        var tm = await Tm(json);
        var r = PlanValidator.Validate(tm);
        Assert.DoesNotContain(r.Errors, e => e.Contains("verification.command"));
    }

    [Fact]
    public async Task Verification_escaped_backslash_n_is_clean()
    {
        // `\\n` (escaped backslash + n) — 인터프리터에 단일 backslash가 전달되므로 SyntaxError 아님
        var json = """
        {"tasks":[{
          "id":"x","title":"X","done":false,"prompt":"p",
          "verification":{"command":"python3 -c \"print('a\\\\nb')\""}
        }]}
        """;
        var tm = await Tm(json);
        var r = PlanValidator.Validate(tm);
        Assert.DoesNotContain(r.Errors, e => e.Contains("verification.command"));
    }
}
