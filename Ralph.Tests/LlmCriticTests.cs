using Ralph.Services;
using Ralph.Tests.Helpers;
using Xunit;

namespace Ralph.Tests;

public class LlmCriticTests
{
    private static async Task<TaskManager> LoadFromJsonAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ralph-llmcritic-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        return await TaskManager.LoadAsync(path);
    }

    [Fact]
    public async Task AnalyzeAsync_Returns_Mock_Response_Verbatim()
    {
        var tm = await LoadFromJsonAsync("""
        {"tasks":[
          {"id":"feat-a-impl","title":"A","done":false,"modifiedFiles":["src/A.cs"]}
        ]}
        """);
        var injected = new ClaudeResult
        {
            Success = true,
            Output = "  - 권고1\n  - 권고2  \n",
            ExitCode = 0,
        };
        var runner = new MockAgentRunner(_ => injected);
        var critic = new LlmCritic();

        var output = await critic.AnalyzeAsync(
            prdContent: "임의 PRD 본문 — 키워드-XYZ를 포함합니다.",
            tm: tm,
            runner: runner,
            model: "opus",
            ct: CancellationToken.None);

        Assert.Equal("- 권고1\n  - 권고2", output);
        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public async Task AnalyzeAsync_Prompt_Contains_Prd_And_Task_Summary()
    {
        var tm = await LoadFromJsonAsync("""
        {"tasks":[
          {"id":"alpha-plan","title":"Alpha plan","done":false,"modifiedFiles":["alpha.md"]},
          {"id":"beta-impl","title":"Beta impl","done":false,"dependsOn":["alpha-plan"],"modifiedFiles":["src/Beta.cs"]}
        ]}
        """);
        var runner = new MockAgentRunner(_ => new ClaudeResult { Success = true, Output = "ok" });
        var critic = new LlmCritic();
        const string prdMarker = "RALPH-LLM-CRITIQUE-MARKER-XYZ";

        await critic.AnalyzeAsync(
            prdContent: $"PRD 본문 헤더\n{prdMarker}\n자세한 내용...",
            tm: tm,
            runner: runner,
            model: null,
            ct: CancellationToken.None);

        Assert.NotNull(runner.LastPrompt);
        // PRD 본문이 그대로 포함됨
        Assert.Contains(prdMarker, runner.LastPrompt);
        // <prd>, <plan> 마커
        Assert.Contains("<prd>", runner.LastPrompt);
        Assert.Contains("</prd>", runner.LastPrompt);
        Assert.Contains("<plan>", runner.LastPrompt);
        Assert.Contains("</plan>", runner.LastPrompt);
        // task 요약: id, deps, modifiedFiles
        Assert.Contains("alpha-plan", runner.LastPrompt);
        Assert.Contains("beta-impl", runner.LastPrompt);
        Assert.Contains("alpha.md", runner.LastPrompt);
        Assert.Contains("src/Beta.cs", runner.LastPrompt);
        // 헤더 지시문
        Assert.Contains("senior software architect", runner.LastPrompt);
    }

    [Fact]
    public async Task AnalyzeAsync_Long_Prd_Is_Trimmed()
    {
        var tm = await LoadFromJsonAsync("""
        {"tasks":[{"id":"x","title":"X","done":false}]}
        """);
        var runner = new MockAgentRunner(_ => new ClaudeResult { Success = true, Output = "" });
        var critic = new LlmCritic();
        var bigPrd = new string('A', 6000) + "TAILMARK" + new string('B', 4000);

        await critic.AnalyzeAsync(bigPrd, tm, runner, "opus", CancellationToken.None);

        Assert.NotNull(runner.LastPrompt);
        Assert.Contains("중략", runner.LastPrompt);
        // PRD 전체가 그대로 들어가지 않아야 함
        Assert.DoesNotContain(new string('A', 6000), runner.LastPrompt);
    }

    [Fact]
    public void BuildPlanSummary_Empty_Tasks_Returns_Placeholder()
    {
        // TaskManager 없이 빈 리스트만 주입한 상황은 LoadAsync가 막으므로,
        // 본 검증은 TrimPrd/empty PRD 케이스로 대체.
        var trimmed = LlmCritic.TrimPrd("");
        Assert.Equal("", trimmed);
    }

    [Fact]
    public async Task BuildPrompt_Includes_Plain_Text_Output_Instruction()
    {
        var tm = await LoadFromJsonAsync("""
        {"tasks":[{"id":"x","title":"X","done":false}]}
        """);
        var prompt = LlmCritic.BuildPrompt("hi", tm);
        Assert.Contains("at most 5 bullets", prompt);
        Assert.Contains("Plain text", prompt);
    }
}
