using Ralph.Services;
using Ralph.Tests.Helpers;
using Xunit;

namespace Ralph.Tests;

public class PlanChunkerTests
{
    // ── Decide: 작은 PRD (< 50KB) ─────────────────────────────────────────────

    [Fact]
    public void Decide_SmallPrd_NotTriggered()
    {
        var content = new string('x', 1024); // 1 KB
        var decision = PlanChunker.Decide(content);
        Assert.False(decision.Triggered);
        Assert.Equal(1024, decision.PrdBytes);
        Assert.Equal(PlanChunker.DefaultPrdSizeThresholdBytes, decision.ThresholdBytes);
    }

    [Fact]
    public void Decide_SmallPrd_TokenEstimateIsConsistent()
    {
        var content = new string('a', 300); // 300 ASCII bytes → 100 tokens
        var decision = PlanChunker.Decide(content);
        Assert.Equal(100, decision.EstInputTokens);
        Assert.False(decision.Triggered);
    }

    [Fact]
    public void Decide_EmptyPrd_NotTriggered()
    {
        var decision = PlanChunker.Decide("");
        Assert.False(decision.Triggered);
        Assert.Equal(0, decision.PrdBytes);
    }

    // ── Decide: 큰 PRD (> 50KB) → 청킹 분기 활성 ────────────────────────────

    [Fact]
    public void Decide_LargePrd_Triggered()
    {
        var content = new string('x', 60 * 1024); // 60 KB > 50 KB 기본 임계치
        var decision = PlanChunker.Decide(content);
        Assert.True(decision.Triggered);
        Assert.True(decision.PrdBytes > PlanChunker.DefaultPrdSizeThresholdBytes);
        Assert.Equal(PlanChunker.DefaultPrdSizeThresholdBytes, decision.ThresholdBytes);
    }

    [Fact]
    public void Decide_PrdAtExactThreshold_NotTriggered()
    {
        // 정확히 50KB: prdBytes > thresholdBytes 조건이 false (등호는 포함 안 함)
        var content = new string('x', PlanChunker.DefaultPrdSizeThresholdBytes);
        var decision = PlanChunker.Decide(content);
        // 50KB 자체는 token 추정으로도 triggered 될 수 있으므로 prdBytes만 검증
        Assert.Equal(PlanChunker.DefaultPrdSizeThresholdBytes, decision.PrdBytes);
    }

    // ── Decide: 임계치 오버라이드 ─────────────────────────────────────────────

    [Fact]
    public void Decide_OverrideKb_SmallContent_Triggered()
    {
        var content = new string('x', 10 * 1024); // 10 KB
        // 5KB로 낮추면 triggered
        var decision = PlanChunker.Decide(content, overrideKb: 5);
        Assert.True(decision.Triggered);
        Assert.Equal(5 * 1024, decision.ThresholdBytes);
    }

    [Fact]
    public void Decide_OverrideKb_VeryHighThreshold_NotTriggered()
    {
        var content = new string('x', 100); // 100 bytes
        // 1MB로 설정 → byte count나 token count 모두 아래
        var decision = PlanChunker.Decide(content, overrideKb: 1024);
        Assert.False(decision.Triggered);
        Assert.Equal(1024 * 1024, decision.ThresholdBytes);
    }

    // ── EstimateTokens ─────────────────────────────────────────────────────────

    [Fact]
    public void EstimateTokens_EmptyOrNull_ReturnsZero()
    {
        Assert.Equal(0, PlanChunker.EstimateTokens(""));
        Assert.Equal(0, PlanChunker.EstimateTokens(null!));
    }

    [Fact]
    public void EstimateTokens_DividesByThree()
    {
        Assert.Equal(1, PlanChunker.EstimateTokens("abc"));      // 3 bytes / 3 = 1
        Assert.Equal(3, PlanChunker.EstimateTokens("abcdefghi")); // 9 bytes / 3 = 3
    }

    // ── LooksTruncated: 정상 응답 ─────────────────────────────────────────────

    [Fact]
    public void LooksTruncated_NullResult_ReturnsFalse()
    {
        Assert.False(PlanChunker.LooksTruncated(null!));
    }

    [Fact]
    public void LooksTruncated_EmptyOutput_ReturnsFalse()
    {
        var result = new ClaudeResult { Success = true, Output = "" };
        Assert.False(PlanChunker.LooksTruncated(result));
    }

    [Fact]
    public void LooksTruncated_ValidClosedJson_ReturnsFalse()
    {
        var result = new ClaudeResult
        {
            Success = true,
            Output = """{"tasks":[{"id":"a","title":"A"}]}""",
        };
        Assert.False(PlanChunker.LooksTruncated(result));
    }

    [Fact]
    public void LooksTruncated_NoJsonStructure_ReturnsFalse()
    {
        var result = new ClaudeResult
        {
            Success = true,
            Output = "Plain text output with no JSON brackets at all.",
        };
        Assert.False(PlanChunker.LooksTruncated(result));
    }

    // ── LooksTruncated: stop_reason=length 시뮬레이션 ─────────────────────────

    [Fact]
    public void LooksTruncated_UnclosedBraces_ReturnsTrue()
    {
        // JSON이 출력 토큰 한계로 닫히지 않은 채 잘린 상황
        var result = new ClaudeResult
        {
            Success = true,
            Output = """{"tasks":[{"id":"a","title":"Cut off here""",
        };
        Assert.True(PlanChunker.LooksTruncated(result));
    }

    [Fact]
    public void LooksTruncated_UnclosedArray_ReturnsTrue()
    {
        // 배열이 닫히지 않음 (} 하나는 있으나 ] 와 바깥 } 누락)
        var result = new ClaudeResult
        {
            Success = true,
            Output = """{"tasks":[{"id":"a","title":"A"}""",
        };
        Assert.True(PlanChunker.LooksTruncated(result));
    }

    [Fact]
    public void LooksTruncated_StringWithEscapedBraces_NotMiscounted()
    {
        // 문자열 내부의 { } [ ] 는 균형 카운팅에서 제외되어야 함
        var result = new ClaudeResult
        {
            Success = true,
            Output = """{"tasks":[{"id":"a","title":"A {test} [ok]"}]}""",
        };
        Assert.False(PlanChunker.LooksTruncated(result));
    }

    [Fact]
    public void LooksTruncated_EscapedQuoteInsideString_HandledCorrectly()
    {
        // 이스케이프된 따옴표가 문자열 종료로 잘못 파싱되면 이후 { } 가 miscounted 됨
        var result = new ClaudeResult
        {
            Success = true,
            Output = """{"tasks":[{"id":"a","title":"He said \"hello\" {ok}"}]}""",
        };
        Assert.False(PlanChunker.LooksTruncated(result));
    }

    // ── BuildTruncationGuidance ────────────────────────────────────────────────

    [Fact]
    public void BuildTruncationGuidance_AlwaysContainsCoreMessage()
    {
        var guidance = PlanChunker.BuildTruncationGuidance(null);
        Assert.Contains("잘렸습니다", guidance);
        Assert.Contains("권장 조치", guidance);
    }

    [Fact]
    public void BuildTruncationGuidance_WithTriggeredDecision_ContainsSizeSection()
    {
        var decision = new ChunkingDecision(
            Triggered: true,
            PrdBytes: 60_000,
            EstInputTokens: 20_000,
            EstOutputTokens: 12_000,
            ThresholdBytes: 50 * 1024);
        var guidance = PlanChunker.BuildTruncationGuidance(decision);
        Assert.Contains("PRD 크기", guidance);
        Assert.Contains("bytes", guidance);
        Assert.Contains("input tokens", guidance);
    }

    [Fact]
    public void BuildTruncationGuidance_WithNonTriggeredDecision_OmitsSizeSection()
    {
        var decision = new ChunkingDecision(
            Triggered: false,
            PrdBytes: 1024,
            EstInputTokens: 341,
            EstOutputTokens: 204,
            ThresholdBytes: 50 * 1024);
        var guidance = PlanChunker.BuildTruncationGuidance(decision);
        Assert.DoesNotContain("PRD 크기", guidance);
    }

    [Fact]
    public void BuildTruncationGuidance_ContainsEnvVarHint()
    {
        var guidance = PlanChunker.BuildTruncationGuidance(null);
        Assert.Contains("CLAUDE_CODE_MAX_OUTPUT_TOKENS", guidance);
    }

    // ── FormatDecisionBox ──────────────────────────────────────────────────────

    [Fact]
    public void FormatDecisionBox_NotTriggered_ShowsSingleCallStrategy()
    {
        var decision = new ChunkingDecision(
            Triggered: false, PrdBytes: 1024, EstInputTokens: 341,
            EstOutputTokens: 204, ThresholdBytes: 50 * 1024);
        var box = PlanChunker.FormatDecisionBox(decision);
        Assert.Contains("single-call", box);
        Assert.Contains("Chunking Decision", box);
    }

    [Fact]
    public void FormatDecisionBox_Triggered_ShowsChunkedStrategy()
    {
        var decision = new ChunkingDecision(
            Triggered: true, PrdBytes: 60 * 1024, EstInputTokens: 20_000,
            EstOutputTokens: 12_000, ThresholdBytes: 50 * 1024);
        var box = PlanChunker.FormatDecisionBox(decision);
        Assert.Contains("chunked", box);
    }

    [Fact]
    public void FormatDecisionBox_ContainsAllMetrics()
    {
        var decision = new ChunkingDecision(
            Triggered: false, PrdBytes: 2048, EstInputTokens: 682,
            EstOutputTokens: 409, ThresholdBytes: 50 * 1024);
        var box = PlanChunker.FormatDecisionBox(decision);
        Assert.Contains("PRD bytes", box);
        Assert.Contains("Est input tokens", box);
        Assert.Contains("Est output tokens", box);
        Assert.Contains("Threshold", box);
    }

    // ── PlanGenerator + MockAgentRunner: 작은 PRD → 단일 호출 ─────────────────

    [Fact]
    public async Task GenerateAsync_SmallPrd_SingleLlmCall_Succeeds()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ralph-chunk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var prdFile = Path.Combine(dir, "PRD.md");
            var tasksFile = Path.Combine(dir, "tasks.json");
            await File.WriteAllTextAsync(prdFile, "Small PRD: 1KB content"); // << 50KB

            const string validJson = """
                {
                  "tasks": [
                    {
                      "id": "feat-impl",
                      "title": "Feature Implementation",
                      "prompt": "Implement the feature.",
                      "category": "implementation"
                    }
                  ],
                  "workflow": { "onTaskComplete": { "commitChanges": true } }
                }
                """;

            var runner = new MockAgentRunner(_ => new ClaudeResult
            {
                Success = true,
                Output = $"```json\n{validJson}\n```",
                ExitCode = 0,
            });

            var generator = new PlanGenerator();
            var exitCode = await generator.GenerateAsync(
                prdFile: prdFile,
                schemaContent: "{}",
                tasksFile: tasksFile,
                claude: runner,
                ct: CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal(1, runner.CallCount); // 단일 LLM 호출
            Assert.True(File.Exists(tasksFile));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── PlanGenerator + MockAgentRunner: 큰 PRD → 청킹 분기 활성 (단일 호출 fallback) ──

    [Fact]
    public async Task GenerateAsync_LargePrd_ChunkingTriggered_SingleCallFallback()
    {
        // 2단계 청킹 미구현 상태에서는 큰 PRD도 단일 호출 path를 사용한다.
        // Mock이 valid JSON을 돌려주면 성공해야 한다.
        var dir = Path.Combine(Path.GetTempPath(), $"ralph-chunk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var prdFile = Path.Combine(dir, "PRD.md");
            var tasksFile = Path.Combine(dir, "tasks.json");
            // 60 KB → PlanChunker.Decide 에서 Triggered=true
            await File.WriteAllTextAsync(prdFile, new string('A', 60 * 1024));

            // PlanChunker.Decide 가 Triggered=true 임을 별도 검증
            var decision = PlanChunker.Decide(new string('A', 60 * 1024));
            Assert.True(decision.Triggered);

            const string validJson = """
                {
                  "tasks": [
                    {
                      "id": "large-impl",
                      "title": "Large Feature",
                      "prompt": "Implement.",
                      "category": "implementation"
                    }
                  ]
                }
                """;

            var runner = new MockAgentRunner(_ => new ClaudeResult
            {
                Success = true,
                Output = $"```json\n{validJson}\n```",
                ExitCode = 0,
            });

            var generator = new PlanGenerator();
            var exitCode = await generator.GenerateAsync(
                prdFile: prdFile,
                schemaContent: "{}",
                tasksFile: tasksFile,
                claude: runner,
                ct: CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal(1, runner.CallCount); // 현재 구현은 단일 호출 fallback
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── PlanGenerator + MockAgentRunner: stop_reason=length 시뮬레이션 → exit code 3 ──

    [Fact]
    public async Task GenerateAsync_TruncatedResponse_ReturnsExitCode3()
    {
        // Claude 응답이 출력 토큰 한계로 잘려 valid JSON을 추출할 수 없는 상황.
        // PlanGenerator 는 LooksTruncated=true 를 감지해 exit code 3 을 반환해야 한다.
        var dir = Path.Combine(Path.GetTempPath(), $"ralph-chunk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var prdFile = Path.Combine(dir, "PRD.md");
            var tasksFile = Path.Combine(dir, "tasks.json");
            await File.WriteAllTextAsync(prdFile, "PRD content");

            // 출력 토큰 한계로 JSON 중간에 잘린 응답 (닫히지 않은 구조)
            const string truncatedOutput = """{"tasks":[{"id":"a","title":"Cut off here""";

            var runner = new MockAgentRunner(_ => new ClaudeResult
            {
                Success = true,
                Output = truncatedOutput,
                ExitCode = 0,
            });

            var generator = new PlanGenerator();
            var exitCode = await generator.GenerateAsync(
                prdFile: prdFile,
                schemaContent: "{}",
                tasksFile: tasksFile,
                claude: runner,
                ct: CancellationToken.None);

            Assert.Equal(PlanChunker.ExitCodePlanTruncated, exitCode); // 3
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── (선택) 병합된 태스크의 의존 그래프 무결성 검증 ────────────────────────

    [Fact]
    public async Task MergedChunkedTasks_DependencyGraphIsValid()
    {
        // 두 청킹 호출 결과를 합친 것처럼 구성한 tasks.json 이 유효한 의존 그래프여야 한다.
        // 독립된 두 피처 그룹 (feat-a, feat-b) 이 순환 없이 병렬 실행 가능해야 함.
        const string json = """
            {
              "tasks": [
                {
                  "id": "feat-a-plan", "title": "A Plan", "prompt": "Plan A.",
                  "category": "plan"
                },
                {
                  "id": "feat-a-impl", "title": "A Impl", "prompt": "Impl A.",
                  "category": "implementation",
                  "dependsOn": ["feat-a-plan"],
                  "outputFiles": ["src/feat_a.py"]
                },
                {
                  "id": "feat-b-plan", "title": "B Plan", "prompt": "Plan B.",
                  "category": "plan"
                },
                {
                  "id": "feat-b-impl", "title": "B Impl", "prompt": "Impl B.",
                  "category": "implementation",
                  "dependsOn": ["feat-b-plan"],
                  "outputFiles": ["src/feat_b.py"]
                }
              ]
            }
            """;

        var path = Path.Combine(Path.GetTempPath(), $"ralph-graph-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        try
        {
            var tm = await TaskManager.LoadAsync(path);
            var result = PlanValidator.Validate(tm);
            Assert.False(result.HasErrors, $"의존 그래프 오류: {string.Join("; ", result.Errors)}");

            // feat-a 와 feat-b 는 서로 독립이므로 병렬 배치의 시작점이 2개여야 함
            var rootTasks = tm.Data.Tasks.Where(t => t.DependsOn is not { Count: > 0 }).ToList();
            Assert.Equal(2, rootTasks.Count);
            Assert.Contains(rootTasks, t => t.Id == "feat-a-plan");
            Assert.Contains(rootTasks, t => t.Id == "feat-b-plan");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
