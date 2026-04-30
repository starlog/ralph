using System.Text;

namespace Ralph.Services;

/// <summary>
/// PlanChunker.Decide 결과. 임계치 초과 여부와 추정치를 함께 노출해 진단 메시지/박스에 사용.
/// </summary>
public sealed record ChunkingDecision(
    bool Triggered,
    int PrdBytes,
    int EstInputTokens,
    int EstOutputTokens,
    int ThresholdBytes);

/// <summary>
/// 대형 PRD 청킹 1차 PR — 임계치 평가 + truncation 휴리스틱 + 안내문 빌더.
/// 본격 2단계 청킹(outline → per-area)은 후속 PR(<see cref="PlanGenerator"/>의 chunked path)에서.
/// </summary>
public static class PlanChunker
{
    public const int DefaultPrdSizeThresholdBytes = 50 * 1024;
    public const int DefaultEstimatedInputTokens = 25_000;
    public const int DefaultEstimatedOutputTokens = 40_000;

    /// <summary>
    /// 출력 토큰 한계로 plan 응답이 잘렸을 때 반환되는 종료 코드. 일반 실패(1)와 구분해
    /// CI 스크립트/사용자가 "재시도해도 같은 자리에서 잘림 — PRD 분할 필요" 임을 식별 가능.
    /// </summary>
    public const int ExitCodePlanTruncated = 3;

    private const string EnvThresholdKb = "RALPH_PLAN_CHUNK_THRESHOLD_KB";
    private const string EnvMaxOutputTokens = "CLAUDE_CODE_MAX_OUTPUT_TOKENS";
    private const int DefaultMaxOutputTokens = 65536;

    public static ChunkingDecision Decide(string prdContent, int? overrideKb = null)
    {
        var prdBytes = Encoding.UTF8.GetByteCount(prdContent ?? "");
        var thresholdKb = overrideKb ?? FromEnv() ?? (DefaultPrdSizeThresholdBytes / 1024);
        var thresholdBytes = thresholdKb * 1024;
        var estInTok = EstimateTokens(prdContent ?? "");
        // 출력은 입력의 60% 정도로 거칠게 추정 (tasks.json은 PRD보다 짧지만 schema/prompt 오버헤드 고려).
        var estOutTok = (int)(prdBytes * 0.6 / 3);
        var triggered = prdBytes > thresholdBytes
                       || estInTok > DefaultEstimatedInputTokens
                       || estOutTok > DefaultEstimatedOutputTokens;
        return new ChunkingDecision(triggered, prdBytes, estInTok, estOutTok, thresholdBytes);
    }

    /// <summary>
    /// 단순 byte/3 토큰 추정. 영문은 약간 과대, 한국어는 byte 기준이 더 보수적이라 안전 측.
    /// </summary>
    public static int EstimateTokens(string text)
        => Encoding.UTF8.GetByteCount(text ?? "") / 3;

    private static int? FromEnv()
    {
        var s = Environment.GetEnvironmentVariable(EnvThresholdKb);
        return int.TryParse(s, out var v) && v > 0 ? v : null;
    }

    /// <summary>
    /// Claude 응답이 출력 토큰 한계로 잘렸을 가능성을 감지. 호출자는 valid JSON 추출에
    /// 실패한 결과(file fallback 포함)에 대해서만 호출해야 한다 — 정상 파싱된 응답은 false.
    ///
    /// 우선순위:
    /// 1. (향후) result.StopReason == "max_tokens" — 현재 ClaudeResult에 미노출이라 적용 안 됨.
    /// 2. 출력 길이가 MAX_OUTPUT_TOKENS × 3 × 0.9 byte 이상 — 한계에 근접.
    /// 3. JSON 구조가 닫히지 않은 채로 끝남 (brace/bracket 불균형).
    /// </summary>
    public static bool LooksTruncated(ClaudeResult result)
    {
        if (result is null || string.IsNullOrEmpty(result.Output)) return false;

        var maxTokens = ParseMaxOutputTokens();
        // MAX_OUTPUT_TOKENS의 90% 길이를 byte 기준으로 환산 (1 token ≈ 3 byte 추정).
        var sizeThreshold = (long)(maxTokens * 3L * 0.9);
        var outputBytes = Encoding.UTF8.GetByteCount(result.Output);
        if (outputBytes >= sizeThreshold) return true;

        if (HasUnclosedJsonStructure(result.Output)) return true;

        return false;
    }

    private static int ParseMaxOutputTokens()
    {
        var s = Environment.GetEnvironmentVariable(EnvMaxOutputTokens);
        return int.TryParse(s, out var v) && v > 0 ? v : DefaultMaxOutputTokens;
    }

    /// <summary>
    /// 출력에 첫 '{'가 등장한 이후의 brace/bracket 균형을 단순 카운팅. 문자열/이스케이프는
    /// 처리하되 주석은 신경 쓰지 않는다(JSON에 주석 없음). 균형이 양수로 끝나면 unclosed.
    /// </summary>
    private static bool HasUnclosedJsonStructure(string output)
    {
        var firstBrace = output.IndexOf('{');
        if (firstBrace < 0) return false;
        var braceDelta = 0;
        var bracketDelta = 0;
        var inString = false;
        var escape = false;
        for (var i = firstBrace; i < output.Length; i++)
        {
            var c = output[i];
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            switch (c)
            {
                case '{': braceDelta++; break;
                case '}': braceDelta--; break;
                case '[': bracketDelta++; break;
                case ']': bracketDelta--; break;
            }
        }
        return braceDelta > 0 || bracketDelta > 0;
    }

    /// <summary>
    /// 잘림 감지 시 사용자에게 보여줄 안내 — Spectre.Console Markup 사용. 호출자는
    /// AnsiConsole.Markup으로 한 번에 출력해도 되고, 줄 단위로 출력해도 된다.
    /// </summary>
    public static string BuildTruncationGuidance(ChunkingDecision? decision)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[red]Plan generation 응답이 출력 토큰 한계로 잘렸습니다.[/]");
        if (decision is { Triggered: true })
        {
            sb.AppendLine(
                $"[dim]PRD 크기: {decision.PrdBytes:N0} bytes "
                + $"(≈ {decision.EstInputTokens:N0} input tokens, "
                + $"est. ≈ {decision.EstOutputTokens:N0} output tokens)[/]");
        }
        sb.AppendLine("[yellow]권장 조치:[/]");
        sb.AppendLine("  1. PRD를 영역별로 2~4개 파일로 분할 후 각각 [green]ralph --plan[/] 실행");
        sb.AppendLine("  2. 또는 PRD의 비핵심 컨텍스트를 줄여 25k token 미만으로 정리");
        sb.AppendLine("  3. 환경변수 [green]CLAUDE_CODE_MAX_OUTPUT_TOKENS[/]로 출력 한계 상향 (모델별 상한 확인 필요)");
        return sb.ToString();
    }

    /// <summary>
    /// Decision 박스 (Spectre Markup). PlanGenerator는 호출 시작 시, --plan-prompt는
    /// prompt 본문 앞에 출력해 사용자가 단일/청킹 전략 분기를 한눈에 보도록 한다.
    /// </summary>
    public static string FormatDecisionBox(ChunkingDecision decision)
    {
        var strategy = decision.Triggered
            ? "[yellow]chunked (will split into outline + per-area calls — not yet implemented in this build, "
              + "fallback guidance will be shown if response is truncated)[/]"
            : "[green]single-call (under threshold)[/]";
        var sb = new StringBuilder();
        sb.AppendLine("[blue]── Chunking Decision ──[/]");
        sb.AppendLine($"  PRD bytes        : {decision.PrdBytes:N0}");
        sb.AppendLine($"  Est input tokens : ~{decision.EstInputTokens:N0}");
        sb.AppendLine($"  Est output tokens: ~{decision.EstOutputTokens:N0}");
        sb.AppendLine($"  Threshold (KB)   : {decision.ThresholdBytes / 1024}");
        sb.AppendLine($"  Strategy         : {strategy}");
        return sb.ToString();
    }
}
