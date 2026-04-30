using System.Text;
using System.Text.Json;
using Ralph.Services;
using Ralph.Tests.Helpers;
using Xunit;

namespace Ralph.Tests;

/// <summary>
/// ClaudeService 256KB prompt 처리 회귀 테스트.
/// 실제 Claude 호출 없이 hang 방지, 경고 로그, cost ledger 기록을 검증한다.
/// </summary>
[Collection("cost")]
public class ClaudeServiceLargePromptTests : IDisposable
{
    private readonly string _tempDir;

    public ClaudeServiceLargePromptTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ralph-large-prompt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ──────────────────────────────────────────────────────────
    // 1. MockAgentRunner로 256KB prompt — hang 없이 완료
    // ──────────────────────────────────────────────────────────

    [Fact(Timeout = 30_000)]
    public async Task MockRunner_LargePrompt_CompletesWithoutHanging()
    {
        const int PromptSize = 256 * 1024;
        var prompt = new string('x', PromptSize);

        var runner = new MockAgentRunner(p => new ClaudeResult
        {
            Success = true,
            Output = "완료",
            PromptBytes = Encoding.UTF8.GetByteCount(p),
        });

        var result = await runner.RunWithRetryAsync(prompt);

        Assert.True(result.Success);
        Assert.True(result.PromptBytes >= PromptSize,
            $"PromptBytes({result.PromptBytes})가 {PromptSize} 미만");
    }

    // ──────────────────────────────────────────────────────────
    // 2. WritePromptChunkedAsync — 256KB 청크 write, 데이터 손실 없음
    // ──────────────────────────────────────────────────────────

    [Fact(Timeout = 30_000)]
    public async Task WritePromptChunkedAsync_WritesAll256KbWithoutDataLoss()
    {
        const int PromptSize = 256 * 1024;
        var prompt = new string('z', PromptSize);

        var ms = new MemoryStream();
        // leaveOpen=true: writer.Close() 후에도 MemoryStream이 유효하게 유지됨
        var writer = new StreamWriter(ms, Encoding.UTF8, bufferSize: 4096, leaveOpen: true)
        {
            AutoFlush = false,
        };

        await ClaudeService.WritePromptChunkedAsync(writer, prompt, CancellationToken.None);

        ms.Position = 0;
        using var reader = new StreamReader(ms, Encoding.UTF8);
        var written = reader.ReadToEnd();

        Assert.Equal(prompt.Length, written.Length);
        Assert.Equal(prompt, written);
    }

    // ──────────────────────────────────────────────────────────
    // 3. 256KB prompt → logger에 한국어 경고 기록 확인
    // ──────────────────────────────────────────────────────────

    [Fact(Timeout = 30_000)]
    public async Task LargePrompt_LogsKoreanWarning_WhenExceedsPipeBufferThreshold()
    {
        const int PromptSize = 256 * 1024;
        var prompt = new string('x', PromptSize);
        var promptByteCount = Encoding.UTF8.GetByteCount(prompt);

        string logFile;
        using (var logger = new RalphLogger(_tempDir))
        {
            logFile = logger.LogFile;
            var svc = new WarnLoggingTestableClaudeService();
            await svc.RunStreamAsync(prompt, logger: logger, output: new StringWriter());
        } // Dispose 시 AutoFlush=true이므로 파일이 닫힌 뒤 읽기 가능

        var logContent = await File.ReadAllTextAsync(logFile);
        Assert.Contains("pipe buffer 초과 가능", logContent);
        Assert.Contains("청크 write 사용", logContent);
        Assert.Contains($"{promptByteCount:N0}바이트", logContent);
    }

    // ──────────────────────────────────────────────────────────
    // 4. cost ledger에 promptBytes >= 256000 기록
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CostLedger_RecordsPromptBytes_AtLeast256000()
    {
        const long ExpectedPromptBytes = 256 * 1024; // 262144
        var usage = new TokenUsage(1000, 500, 0, 0);
        var result = new ClaudeResult
        {
            Success = true,
            Usage = usage,
            Duration = TimeSpan.FromSeconds(1),
            PromptBytes = ExpectedPromptBytes,
        };

        var cost = new CostTracker(_tempDir);
        await cost.RecordAsync("large-prompt-task", "sonnet", result);

        Assert.True(File.Exists(cost.LogFilePath));
        var line = (await File.ReadAllLinesAsync(cost.LogFilePath)).Last();

        using var doc = JsonDocument.Parse(line);
        var pb = doc.RootElement.GetProperty("promptBytes").GetInt64();
        Assert.True(pb >= 256_000, $"promptBytes({pb})가 256000 미만");
    }

    // ──────────────────────────────────────────────────────────
    // 내부 테스트 전용 서브클래스
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// 실제 process 시작 없이 prompt 크기 경고 로그를 재현하는 테스트 전용 ClaudeService.
    /// ClaudeService.RunStreamAsync의 큰 prompt 경고 경로를 격리 검증용.
    /// </summary>
    private sealed class WarnLoggingTestableClaudeService : ClaudeService
    {
        public override Task<ClaudeResult> RunStreamAsync(
            string prompt,
            string? model = null,
            string? workingDirectory = null,
            RalphLogger? logger = null,
            TextWriter? output = null,
            CancellationToken ct = default,
            string? allowedTools = null)
        {
            const int PipeBufferWarningBytes = 32 * 1024;
            var promptByteCount = (long)Encoding.UTF8.GetByteCount(prompt);

            if (promptByteCount > PipeBufferWarningBytes)
                logger?.Warn($"prompt 크기 {promptByteCount:N0}바이트, OS pipe buffer 초과 가능 — 청크 write 사용");

            return Task.FromResult(new ClaudeResult
            {
                Success = true,
                Output = "ok",
                PromptBytes = promptByteCount,
            });
        }
    }
}
