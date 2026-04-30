using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

/// <summary>
/// PlanGenerator.BuildPlanPrompt 가 호스트 환경 정보를 정확히 주입하는지 검증.
/// 동기: Windows에서 plan을 생성하면 verification.command가 `python3`을 쓰도록 LLM이 작성하는데,
/// `python3.exe`는 보통 Microsoft Store 스텁이라 항상 exit 9009로 실패한다. 프롬프트가 호스트 OS와
/// 권장 인터프리터를 명시해야 LLM이 올바른 바이너리 이름을 선택한다.
/// </summary>
public class PlanGeneratorPromptTests
{
    [Fact]
    public void Prompt_includes_host_environment_section()
    {
        var prompt = PlanGenerator.BuildPlanPrompt(
            prdFilePath: "/tmp/PRD.md",
            schemaContent: "{}",
            tasksFilePath: "/tmp/tasks.json");

        Assert.Contains("## Host environment", prompt);
        Assert.Contains("Operating system:", prompt);
        Assert.Contains("Python interpreter:", prompt);
    }

    [Fact]
    public void Prompt_advertises_host_appropriate_python_command()
    {
        var prompt = PlanGenerator.BuildPlanPrompt(
            prdFilePath: "/tmp/PRD.md",
            schemaContent: "{}",
            tasksFilePath: "/tmp/tasks.json");

        if (OperatingSystem.IsWindows())
        {
            // Windows: `python` (NOT `python3`)을 명시해야 하고, MS Store 스텁 경고도 포함.
            Assert.Contains("`python`", prompt);
            Assert.Contains("Microsoft Store stub", prompt);
            Assert.Contains("9009", prompt);
        }
        else
        {
            Assert.Contains("`python3`", prompt);
        }
    }

    [Fact]
    public void Prompt_forbids_absolute_paths_in_generated_task_prompts()
    {
        // 동기: planner 가 절대 경로(`D:\t8`)를 노출하면 모델이 그것을 각 task 의 prompt 에
        // 그대로 박아넣어, worktree 에서 실행될 때 파일이 메인 레포로 새고 verification 이
        // 못 찾아 실패한다. 가드레일로 "상대 경로만 쓸 것" 규칙이 반드시 prompt 에 있어야 함.
        var prompt = PlanGenerator.BuildPlanPrompt(
            prdFilePath: "/tmp/PRD.md",
            schemaContent: "{}",
            tasksFilePath: "/tmp/tasks.json");

        Assert.Contains("use ONLY relative paths", prompt);
        Assert.Contains(".ralph-worktrees/{taskId}/", prompt);
        Assert.Contains("verification command", prompt);
    }

    [Fact]
    public void Prompt_positive_examples_use_host_python_command()
    {
        // 13/14번 규칙의 ALLOWED/PREFERRED 예시는 호스트에 맞게 치환되어야 한다.
        // FORBIDDEN 예시는 OS 무관한 \n 이스케이프 이슈를 보여주는 것이므로 literal `python3` 유지.
        var prompt = PlanGenerator.BuildPlanPrompt(
            prdFilePath: "/tmp/PRD.md",
            schemaContent: "{}",
            tasksFilePath: "/tmp/tasks.json");

        var pythonCmd = OperatingSystem.IsWindows() ? "python" : "python3";

        // PREFERRED smoke test 예시 (compileall) — 호스트 python 사용.
        Assert.Contains($"`{pythonCmd} -m compileall -q .`", prompt);

        // ALLOWED inline 검증 예시 — 호스트 python 사용.
        Assert.Contains($"`{pythonCmd} -c \"from m import f", prompt);
    }
}
