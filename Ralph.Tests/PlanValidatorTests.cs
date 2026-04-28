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
    public async Task Empty_prompt_is_warning_not_error()
    {
        var tm = await Tm("""
        {"tasks":[{"id":"a","title":"A","done":false}]}
        """);
        var r = PlanValidator.Validate(tm);
        Assert.False(r.HasErrors);
        Assert.True(r.HasWarnings);
        Assert.Contains(r.Warnings, w => w.Contains("prompt가 비어"));
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
}
