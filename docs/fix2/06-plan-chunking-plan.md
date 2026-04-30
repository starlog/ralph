# fix2 #6 — 대형 PRD 청킹 전략 설계

## 0. 한 줄 요약

PRD가 임계치를 넘으면 plan 호출을 **개요 → 영역별 상세 → 병합 + 그래프 검증** 2단계로
나누어 출력 토큰 한계로 tasks.json 이 잘리는 사고를 막는다. 임계치 미만은 현행 단일
호출을 그대로 유지하며, 1차 릴리스에서는 **단순 fallback (truncation 감지 + 사용자
안내)** 만 도입하고, 본격 2단계 청킹은 후속 PR로 분리한다.

---

## 1. 배경

### 1.1 현재 흐름 (`Ralph/Services/PlanGenerator.cs`)

`GenerateAsync` 한 번 실행 시:

1. `BuildPlanPrompt(prdFilePath, schemaContent, tasksFilePath, categories)` 로 PRD 경로
   + 임베디드 JSON Schema + 4-stage 가이드를 합쳐 단일 prompt 생성.
2. `claude.RunStreamAsync(prompt, model: "opus", ...)` 한 번 호출. Claude 가 직접
   `Write` 툴로 `tasks.json` 을 쓰거나 stdout 에 fenced JSON 을 출력.
3. `ExtractJson` → `JsonDocument.Parse` → `TasksFile` deserialize → `PlanValidator`.
4. 실패 시 `BuildCorrectionPrompt` 으로 errors 를 prepend 해 최대 2회 재호출
   (`PlanCommand` 의 correction loop).
5. 최종 통과 결과를 atomic write (tmp + rename) 로 `tasks.json` 에 저장.

### 1.2 PRD 가 커질 때의 실패 모드

- `CLAUDE_CODE_MAX_OUTPUT_TOKENS` 기본값 65536 (`ClaudeService.cs:176`). 100KB+
  PRD 에 대해 작성된 tasks.json 이 이 한계를 넘으면 응답이 중간에 끊긴다.
- 잘린 JSON 은 fenced block 안에서 끝나지 않거나 마지막 task 가 잘려 schema
  validation 에서 **invalid JSON / missing field** 로 실패.
- correction loop 가 동일한 PRD 로 같은 호출을 반복하므로 같은 자리에서 또 잘린다.
- `ClaudeResult` 에 `stop_reason` 필드가 **현재 없음** — Claude Code stream-json
  의 `result` 메시지에서 `usage` 만 파싱(`ClaudeService.cs:408-422`). truncation
  여부는 휴리스틱(파싱 실패 + output 길이 ≈ MAX_OUTPUT_TOKENS) 으로 추정해야 한다.

### 1.3 영향 범위

- `Ralph/Services/PlanGenerator.cs` — 단일 호출 path 분기 추가.
- `Ralph/Services/ClaudeService.cs` — `result.stop_reason` 노출(있으면) 또는 동등
  지표 노출.
- `Ralph/Services/PlanChunker.cs` (신규, 후속 PR) — 2단계 전략 구현 자리.
- `Ralph/Commands/PlanPromptCommand.cs` — `--plan-prompt` 가 청킹 사용 여부 +
  계획된 sub-call 목록을 미리 보여주도록 확장.

---

## 2. 임계치

### 2.1 측정 기준

Tokenizer 의존을 피하기 위해 **PRD 파일 크기(byte)** 와 **추정 토큰 수** 두 가지
신호 중 큰 쪽을 사용한다.

| 신호 | 임계치 | 근거 |
|---|---|---|
| PRD 파일 크기 | `> 50 KB` | 경험적으로 50KB 이상에서 출력 토큰 부족 사례 보고 (fix2.md #6). |
| 추정 입력 토큰 | `> 25_000` tokens | 1 token ≈ 3 chars (영문) / 1 token ≈ 1.5 chars (한글). PRD 가 한국어/혼합이면 byte 기준이 더 보수적. |
| 추정 출력 토큰 | `> 40_000` tokens | `MAX_OUTPUT_TOKENS=65536` 의 60% 이상이면 안전 마진 부족. PRD bytes × 0.6 으로 거칠게 추정. |

세 임계치는 모두 `PlanChunker` 내부 const 로 고정하되 환경변수
`RALPH_PLAN_CHUNK_THRESHOLD_KB` 로 PRD-byte 임계치만 override 가능 (테스트/실험용).

### 2.2 임계치 평가 함수

```csharp
public static class PlanChunker
{
    public const int DefaultPrdSizeThresholdBytes = 50 * 1024;
    public const int DefaultEstimatedInputTokens = 25_000;
    public const int DefaultEstimatedOutputTokens = 40_000;

    public static ChunkingDecision Decide(string prdContent, int? overrideKb = null)
    {
        var prdBytes = Encoding.UTF8.GetByteCount(prdContent);
        var threshold = (overrideKb ?? FromEnv() ?? DefaultPrdSizeThresholdBytes / 1024) * 1024;
        var estInTok = EstimateTokens(prdContent);          // bytes / 3
        var estOutTok = (int)(prdBytes * 0.6 / 3);          // 휴리스틱
        var triggered = prdBytes > threshold
                       || estInTok > DefaultEstimatedInputTokens
                       || estOutTok > DefaultEstimatedOutputTokens;
        return new ChunkingDecision(triggered, prdBytes, estInTok, estOutTok);
    }
}
```

`PlanGenerator.GenerateAsync` 진입 시 `PlanChunker.Decide` 한 번 호출하고
`triggered=false` 이면 **현행 단일 호출 path 그대로** 진행.

---

## 3. 2단계 전략 (본격 청킹)

> 1차 릴리스에서는 §4 의 단순 fallback 만 구현한다. 본 절은 후속 PR 의 명세로
> 보존하며, `PlanChunker` 의 책임 범위 (§6) 에 표시된 범위까지 포함한다.

### 3.1 흐름도

```
                ┌──────────────────────────────┐
PRD (large) →   │  PlanChunker.Decide          │ → triggered=true
                └──────────────┬───────────────┘
                               │
        ┌──────────────────────▼─────────────────────┐
        │ Stage A — 개요(outline) 호출               │
        │  prompt: PRD + Outline schema              │
        │  response: { areas: [{ id, title,         │
        │              taskCount, dependsOn:[],      │
        │              summary }] }                  │
        └──────────────────────┬─────────────────────┘
                               │  areas[]
                               ▼
        ┌────────────────────────────────────────────┐
        │ Stage B — 영역별 상세 호출 (per area)       │
        │   for each area:                           │
        │     prompt: PRD subset hint + outline +    │
        │             schema(부분) + area scope       │
        │     response: { tasks: [...] } (해당 area) │
        │   병렬 호출 가능 (Task.WhenAll, opus 동시 N)│
        └──────────────────────┬─────────────────────┘
                               │  tasks per area
                               ▼
        ┌────────────────────────────────────────────┐
        │ Stage C — 병합 + 검증                       │
        │  - task ID 충돌 검사 (다음 절)             │
        │  - cross-area dependsOn 재바인딩            │
        │  - PlanValidator 통과 확인                 │
        │  - 통과 못하면 correction loop (단일 호출) │
        └──────────────────────┬─────────────────────┘
                               │
                               ▼
                       atomic write tasks.json
```

### 3.2 Stage A 개요 응답 schema (내부)

```json
{
  "areas": [
    {
      "id": "auth",
      "title": "인증/세션 도메인",
      "summary": "JWT 발급, refresh, 로그아웃 처리.",
      "estimatedTaskCount": 4,
      "dependsOn": []
    },
    {
      "id": "billing",
      "title": "결제 모듈",
      "summary": "...",
      "estimatedTaskCount": 6,
      "dependsOn": ["auth"]
    }
  ]
}
```

`areas[].id` 는 **task id prefix** 로 사용되며 (`auth-plan`, `billing-impl`) Stage B
prompt 에 박아넣어 ID 네임스페이스 충돌을 미연에 방지한다.

### 3.3 Stage B prompt 변형

기존 `BuildPlanPrompt` 에 다음을 prepend 한 변형 (`BuildAreaPlanPrompt`) 을 사용:

- **Area scope** 블록: 이번 호출이 책임지는 area 와 그 summary, estimatedTaskCount.
- **Outline 전체**: 다른 area 의 id/summary 를 함께 넘겨 cross-area dependsOn 을
  올바르게 작성하도록 유도.
- **ID prefix 강제**: "이 호출이 생성하는 task id 는 `<areaId>-` 로 시작해야 한다."
- **schema 는 동일** — area 별 호출도 같은 tasks.json schema 의 부분집합으로 응답.

### 3.4 병합 (Stage C) 의 책임

| 단계 | 처리 |
|---|---|
| ID 충돌 검사 | area 별 prefix 강제로 1차 방어. 그래도 중복이면 `{area}-{taskId}` 로 자동 rename + 모든 dependsOn 참조도 일괄 치환. |
| dangling dependsOn | 다른 area 의 task 를 가리키지만 outline 의 area-level dependsOn 과 모순되면 경고 후 outline 우선으로 제거. 완전히 unknown 이면 PlanValidator 가 잡도록 그대로 보존 → correction loop 로 보낸다. |
| projectName/version/workflow | 첫 area 응답의 값을 truth source 로 채택. 후속 area 응답의 동일 필드는 무시. |
| workflow.smokeTest/categories | 마찬가지로 첫 응답 채택 후 outline 에 명시한 값으로 override 가능. |

### 3.5 동시 호출 정책

- `Task.WhenAll` 로 area 호출을 병렬화하되 **동시 호출 수는 2** 로 캡 (opus 비용/
  rate-limit 고려). `RALPH_PLAN_CHUNK_PARALLEL` 으로 1~4 사이 override 가능.
- 한 area 라도 실패하면 즉시 cancel + 단순 fallback (§4) 로 degrade.

---

## 4. 단순 fallback (1차 릴리스 범위)

본격 청킹은 별도 PR로 미루고, 우선 **truncation 감지 + 사용자 안내** 만 도입한다.

### 4.1 truncation 감지 인터페이스

`ClaudeResult` 에 다음 필드 추가:

```csharp
public class ClaudeResult
{
    // ... 기존 필드 ...

    /// <summary>
    /// stream-json result 메시지의 stop_reason. 알려진 값: "end_turn", "max_tokens",
    /// "stop_sequence", "tool_use". null = 미지원/미파싱.
    /// </summary>
    public string? StopReason { get; init; }
}
```

`ClaudeService.cs:397-423` 의 `result` 분기에서 `root.TryGetProperty("stop_reason", ...)`
또는 `root.GetProperty("message").GetProperty("stop_reason")` 을 안전 추출. Claude
Code 버전별로 위치가 다를 수 있으므로 두 경로 모두 시도하고 못 찾으면 null.

### 4.2 PlanGenerator 에서의 분기

```csharp
var result = await claude.RunStreamAsync(prompt, model: model, logger: logger, ct: ct);

if (PlanChunker.LooksTruncated(result))
{
    AnsiConsole.MarkupLine("[red]Plan generation 응답이 출력 토큰 한계로 잘렸습니다.[/]");
    AnsiConsole.MarkupLine("[yellow]권장 조치:[/]");
    AnsiConsole.MarkupLine("  1. PRD 를 영역별로 2~4개 파일로 분할 후 각각 --plan");
    AnsiConsole.MarkupLine("  2. 또는 PRD 의 비핵심 컨텍스트를 줄여 25k token 미만으로 정리");
    AnsiConsole.MarkupLine("  3. CLAUDE_CODE_MAX_OUTPUT_TOKENS 환경변수로 출력 한계 상향 (모델별 상한 확인 필요)");
    return ExitCodes.PlanTruncated;   // 신규 별도 종료 코드 (예: 3) — CI 스크립트가 식별 가능
}
```

`LooksTruncated` 판정 우선순위:

1. `result.StopReason == "max_tokens"` — 최우선 신호.
2. `result.Output` 이 fenced JSON 도 아니고 raw JSON 도 아니며 길이가
   `MAX_OUTPUT_TOKENS * 3 * 0.9` 바이트 이상 — 휴리스틱.
3. `JsonDocument.Parse` 실패 + 마지막 line 이 닫히지 않은 brace/bracket — 휴리스틱.

`stop_reason` 이 null 이고 정상 파싱되면 truncation 으로 보지 않는다 (false positive
방지).

### 4.3 안내문이 첫 PR 에 포함되어야 하는 이유

- 사용자가 잘림 사고를 인지하지 못한 채 invalid tasks.json 을 반복 재생성하며 비용을
  태우는 시나리오를 즉시 차단.
- 본격 2단계 청킹은 prompt 설계 / 병합 검증 / 동시성 등 이슈가 많아 별도 PR 로
  보내야 하지만, fallback 안내는 상수 + 한 분기로 가능.

---

## 5. 영역 간 task ID 충돌/의존 보강

본격 2단계 (§3) 도입 시 핵심 위험은 area 별 호출이 서로 모르고 같은 ID 를 만들거나
존재하지 않는 task 에 dependsOn 거는 것. 단계별 방어:

| 방어선 | 위치 | 효과 |
|---|---|---|
| ID prefix 강제 | Stage B prompt | "task id 는 반드시 `<areaId>-` 로 시작" 명문화 + 예시. |
| Outline 전달 | Stage B prompt | 다른 area 의 id/summary 를 함께 보내 cross-area dependsOn 작성 시 정확한 id 사용. |
| 자동 rename | Stage C 병합 | 그래도 충돌 시 `{area}-{taskId}` 로 강제 rename 하고 dependsOn 일괄 치환. |
| `PlanValidator` | Stage C 후 | 기존 cycle/dangling/duplicate 검증 그대로 통과시켜야 통과. 실패 시 correction loop. |
| Outline 의존 우선 | Stage C 병합 | task-level dependsOn 이 outline area-level 의존을 위반하면 task-level 을 끊고 경고만 남긴다 (outline 이 그래프 진실). |

---

## 6. `Ralph/Services/PlanChunker.cs` 책임 범위

### 6.1 1차 PR 범위 (필수)

- `record ChunkingDecision(bool Triggered, int PrdBytes, int EstInTok, int EstOutTok)`
- `static ChunkingDecision Decide(string prdContent, int? overrideKb = null)`
- `static int EstimateTokens(string text)` — `bytes / 3` (단순). 한국어 비중이 높으면
  `bytes / 2` 로 보정하는 후속 개선 가능.
- `static bool LooksTruncated(ClaudeResult result)` — §4.2 휴리스틱.
- `static string BuildTruncationGuidance(ChunkingDecision decision)` — Spectre.Console
  Markup 포함된 멀티라인 메시지.

→ 이 범위만으로 fix2 #6 의 검증 항목 일부 (잘림 감지) 와 회귀 (작은 PRD 무변화) 를
   모두 만족.

### 6.2 후속 PR 범위 (2단계 전략 본격 구현)

- `BuildOutlinePrompt(prdContent, schemaContent)` 와 `BuildAreaPlanPrompt(...)`.
- `record OutlineArea(string Id, string Title, string Summary, int EstimatedTaskCount, IReadOnlyList<string> DependsOn)`.
- `Task<TasksFile> GenerateChunkedAsync(...)` — Stage A → Stage B (Task.WhenAll + cap)
  → Stage C 병합 + 검증.
- 실패 시 §4 fallback 으로 자동 degrade.

### 6.3 PlanGenerator 와의 결합

`PlanGenerator.GenerateAsync` 는 `PlanChunker.Decide` 결과만 보고 분기:

```csharp
var prdContent = await File.ReadAllTextAsync(prdFile, ct);
var decision = PlanChunker.Decide(prdContent);
if (!decision.Triggered)
{
    // 현행 단일 호출 그대로
}
else
{
    // 1차 PR: Decide 의 정보를 사용자에게 보여주고 진행하되,
    //         호출 결과가 truncated 면 §4 안내 후 종료.
    //         (본격 2단계 호출은 §6.2 시점부터 사용)
}
```

---

## 7. `--plan-prompt` 시각화 변경

`Ralph/Commands/PlanPromptCommand.cs` 가 현재 단일 prompt 만 출력하므로 다음을 추가.

### 7.1 1차 PR

prompt 출력 직전에 chunking decision 박스를 출력:

```
┌── Chunking Decision ─────────────────────────────────────────┐
│ PRD bytes        : 12,431                                    │
│ Est input tokens : ~4,143                                    │
│ Est output tokens: ~2,486                                    │
│ Threshold (KB)   : 50                                        │
│ Strategy         : single-call (under threshold)             │
└──────────────────────────────────────────────────────────────┘
```

임계치 초과 시 Strategy 가 `chunked (will split into outline + per-area calls — not
yet implemented in this build, fallback guidance will be shown if response is
truncated)` 로 바뀌고 색상은 yellow.

### 7.2 후속 PR (2단계 구현 후)

Strategy 가 `chunked` 일 때 outline 의 area 미리보기를 dry-run 으로 한 번 호출하여
다음 형태로 표시:

```
Planned chunked calls:
  1. Outline call (1 round-trip)
  2. Per-area calls (parallel, max 2 in flight):
       - auth        (≈4 tasks)
       - billing     (≈6 tasks, depends on auth)
       - reporting   (≈3 tasks, depends on billing)
  3. Merge + validate
```

`--plan-prompt` 는 실제 호출을 하지 않으므로 area 미리보기는 1차 PR 에서는 생략하고,
Decision 박스만 보여주는 것으로 충분하다.

---

## 8. 회귀: 작은 PRD 동작 불변

### 8.1 보장 조건

- `PlanChunker.Decide(prd).Triggered == false` 이면 `PlanGenerator.GenerateAsync` 는
  현행 코드 path (단일 `claude.RunStreamAsync`, 동일한 prompt, 동일한 atomic write)
  를 byte-for-byte 그대로 실행.
- `BuildPlanPrompt` 의 시그니처/본문은 변경하지 않는다 (chunking 분기는 호출 측에서만).
- `--plan-prompt` 의 prompt 본문도 동일 — Decision 박스는 prompt 출력 **앞**에 추가될
  뿐 prompt 자체는 동일.

### 8.2 회귀 게이트

- 기존 `PlanGeneratorTests` (있으면) 가 모두 통과.
- `PlanChunker.Decide(small).Triggered == false` 단위 테스트로 임계치 경계 보장.
- `dotnet test` 의 build verification 이 깨지지 않을 것.

---

## 9. 테스트 시나리오

### 9.1 단위 테스트 (`Ralph.Tests/PlanChunkerTests.cs` — 신규)

| # | 시나리오 | 기대 |
|---|---|---|
| T1 | 1KB PRD | `Decide(...).Triggered == false` |
| T2 | 49KB PRD | `Decide(...).Triggered == false` (경계) |
| T3 | 51KB PRD | `Decide(...).Triggered == true` |
| T4 | `RALPH_PLAN_CHUNK_THRESHOLD_KB=10` + 11KB PRD | `Triggered == true` |
| T5 | 한국어 100KB PRD | `EstInTok > 25_000` |
| T6 | `LooksTruncated` — `StopReason == "max_tokens"` | `true` |
| T7 | `LooksTruncated` — 정상 파싱 + `StopReason == "end_turn"` | `false` |
| T8 | `LooksTruncated` — `StopReason == null` + 닫히지 않은 `{` 로 끝나는 긴 출력 | `true` (휴리스틱) |
| T9 | `LooksTruncated` — `StopReason == null` + 정상 fenced JSON | `false` |

### 9.2 통합 테스트 (`Ralph.Tests/PlanGeneratorChunkingTests.cs` — 신규)

`IAgentRunner` 를 mock 으로 주입하여 ClaudeService 를 우회.

| # | 시나리오 | mock 응답 | 기대 동작 |
|---|---|---|---|
| I1 | 작은 PRD + 정상 응답 | 정상 fenced tasks.json, `StopReason="end_turn"` | tasks.json atomic write, exit 0. |
| I2 | 작은 PRD + truncated 응답 | 잘린 JSON, `StopReason="max_tokens"` | (단순 fallback path 도 발동) §4.2 안내 + 신규 exit code. |
| I3 | 큰 PRD (60KB) + 정상 응답 | 정상 응답 | (1차 PR) Decision 박스 표시 후 단일 호출 진행, 통과. |
| I4 | 큰 PRD (60KB) + truncated 응답 | 잘린 JSON, `StopReason="max_tokens"` | §4.2 안내 + 신규 exit code, tasks.json 미수정. |
| I5 | 큰 PRD + `StopReason=null` + 휴리스틱 truncation | 닫히지 않은 큰 JSON | T8 동일 — 안내 + exit. |

### 9.3 회귀 테스트

| # | 시나리오 | 기대 |
|---|---|---|
| R1 | 기존 `PlanCommand` 시나리오 (작은 PRD) | 기존과 동일하게 통과. |
| R2 | `--plan-prompt` 작은 PRD | prompt 본문 동일, Decision 박스만 추가. |
| R3 | correction loop (validator errors → 재호출) | 기존 동작 유지. truncation 과 무관. |

### 9.4 수동 검증 (1차 PR 머지 전 1회)

- 합성 100KB PRD (`docs/fix2/06-sample-large-prd.md` 등 별도 산출물로는 만들지 않음;
  로컬에서만 생성) 로 `--plan-prompt` 실행 → Decision 박스 yellow.
- `CLAUDE_CODE_MAX_OUTPUT_TOKENS=2048` 강제로 인위적 truncation 유도 → §4.2 안내가
  뜨는지, exit code 가 0이 아닌 신규 코드인지 확인.

### 9.5 후속 PR (§3 본격 구현) 전용 테스트

- snapshot 테스트: 동일 PRD 를 (1) 단일 호출, (2) 2단계 호출 두 path 로 만들어 task
  id 집합 / dependsOn 그래프 / categories 분포가 일치하는지 set 비교 (순서 무관).
- area 분리 후 cross-area dependsOn 이 끊기지 않는지 (`PlanValidator` 통과).
- ID 충돌 자동 rename 동작 (의도적으로 두 area 가 같은 id 를 만들도록 mock).

---

## 10. 단계별 작업 항목 (구현 PR 분할)

### 10.1 PR-1: 단순 fallback (이 fix2 #6 의 본 구현 범위)

1. `ClaudeResult.StopReason` 필드 추가 + `ClaudeService.cs` result 메시지 파싱.
2. `Ralph/Services/PlanChunker.cs` 신규 — §6.1 범위 (Decide + LooksTruncated +
   BuildTruncationGuidance).
3. `PlanGenerator.GenerateAsync` 에 §4.2 분기 추가.
4. `PlanPromptCommand` 에 §7.1 Decision 박스 추가.
5. 신규 exit code 정의 (Program / CommandDispatcher 의 기존 exit-code 컨벤션 따라).
6. 단위 테스트 §9.1, 통합 테스트 §9.2 의 I1·I2·I4·I5, 회귀 §9.3 R1~R3.
7. CLAUDE.md 의 환경변수 표에 `RALPH_PLAN_CHUNK_THRESHOLD_KB` 추가.

### 10.2 PR-2: 본격 2단계 청킹 (후속)

1. `BuildOutlinePrompt`, `BuildAreaPlanPrompt`.
2. `OutlineArea` record + Stage A/B/C 호출 함수.
3. `PlanGenerator` chunked path 활성화 (Decide.Triggered=true 일 때 단일 호출 대신
   chunked).
4. §3.4 병합 + §5 ID 충돌 보강.
5. `--plan-prompt` 의 §7.2 area 미리보기.
6. snapshot 테스트 §9.5.

PR-1 만 머지된 상태에서도 사용자는 "잘림"을 알아차릴 수 있고, 가이드대로 PRD 를
나눠 재시도할 수 있다 — 이것이 fix2 #6 의 최소 acceptance.

---

## 11. 위험 / 미결 사항

- Claude Code stream-json 의 `stop_reason` 필드 위치/존재 여부는 버전 의존적.
  `LooksTruncated` 의 휴리스틱 fallback 은 false positive 를 만들 수 있다 — I9 테스트로
  방어하되 운영에서 false positive 가 보고되면 임계치 상향 조정.
- 토큰 추정은 `bytes / 3` 거친 휴리스틱. 코드 블록이 많은 PRD 는 과대평가, 한국어
  prose 는 과소평가될 수 있다. PR-1 시점에서는 이 정도로 충분 (안전 마진 60%).
- 2단계 호출의 비용은 단일 호출보다 1.2~1.5배 정도 늘어날 가능성 — 임계치를 너무
  낮게 잡지 않도록 주의 (50KB 가 보수적).
- `--plan-prompt` 의 Decision 박스는 prompt 본문 앞에 출력되므로 기존 사용자의
  스크립트가 stdout 의 첫 줄을 파싱하고 있다면 깨질 수 있다 — 박스를 stderr 로 보낼지
  검토 (CLAUDE.md `DisplayHelpers` 패턴 따라 stdout 유지가 자연스러움).

---

## 12. 산출물 / Acceptance

PR-1 머지 시 만족해야 할 조건:

- [ ] 작은 PRD: 기존 `--plan` / `--plan-prompt` 동작 byte-for-byte 동일 (R1, R2).
- [ ] 큰 PRD + 정상 응답: 단일 호출 그대로 통과 + Decision 박스 노출 (I3).
- [ ] 큰 PRD + truncated 응답: §4.2 안내 + 신규 exit code (I4, I5).
- [ ] 단위 테스트 T1~T9, 통합 I1·I2·I4·I5 통과.
- [ ] CLAUDE.md 환경변수 표 갱신.

PR-2 머지 시 추가 만족 조건은 §3 / §9.5 항목으로 별도 plan 갱신.
