# Ralph 개선 계획 (보완안)

> `improvement.md`의 후속/대안 문서. 코드를 직접 읽고 발견한 **현재 구현의 결함**을 우선 다루며, 그 위에 새 기능을 얹는 순서를 제안한다.
>
> 핵심 입장: **"Ralph가 더 무엇을 해야 하나"보다 "지금 있는 기능부터 제대로 작동시키자"가 먼저다.** Conflict 해결이 사실상 깨져 있고, retry가 학습 없이 동일 prompt를 재시도하며, worktree에서 Claude가 받는 prompt가 너무 빈약한 상태에서 Learning Loop·Sandbox 같은 큰 기능을 얹으면 디버깅 표면이 폭발한다.

---

## 우선순위 기준

- **P0 (Critical Bug / Integrity)** — 현재 코드에 박혀 있는 결함. 수정 안 하면 다른 개선의 기반이 흔들린다.
- **P1 (Quick Win)** — 적은 코드로 큰 가시성/안정성 향상이 나오는 항목.
- **P2 (Adoption)** — 사용자 확보. 사용자 없이는 다른 P0/P1의 피드백 루프가 없다.
- **P3 (Feature)** — `improvement.md`의 큰 기능들. P0~P2 정리 후 진행.

---

## P0-1. Conflict 해결의 작업 디렉토리 누락 (버그)

### 문제

`Ralph/Services/ParallelExecutor.cs:425` `ResolveConflictsWithClaudeAsync`에서 conflict 해결용 Claude를 **`workingDirectory` 인자 없이** 호출한다.

```csharp
var result = await _claude.RunWithRetryAsync(prompt, model: _model, logger: _logger, ct: ct);
```

병합은 base repo에서 일어나는데, ralph가 어디서 실행됐느냐에 따라 Claude는 엉뚱한 디렉토리에서 충돌 마커를 찾게 된다. 또한 `mergeResult.ConflictFiles`의 path가 repo-relative라는 보장이 명시되어 있지 않다. **`conflictStrategy: "claude"`가 실전에서 동작한 적이 있는지 의심된다.**

### 제안

1. `_git.GetRepoRootAsync()` (없으면 추가)로 base repo 절대경로를 얻어 `workingDirectory`로 명시 전달
2. `ConflictFiles`를 항상 repo-relative로 정규화 (이미 그러하다면 단위 테스트로 보호)
3. Conflict 해결 prompt에 다음을 명시:
   - `git status`로 현재 충돌 파일 확인
   - 각 파일의 `<<<<<<<`/`=======`/`>>>>>>>` 마커 모두 제거
   - `git add <file>`로 staging
4. **통합 테스트** — 인위적으로 충돌이 발생하는 fixture repo를 두고 strategy="claude"로 해결되는지 검증

### 영향

지금까지 conflict 해결이 거의 동작 안 했을 가능성이 높음. 수정 후 안정성 + claude strategy를 default로 권장 가능.

---

## P0-2. Retry가 컨텍스트 없이 동일 prompt를 재실행 (반쯤 무용)

### 문제

`Ralph/Services/ClaudeService.cs:343` `RunWithRetryAsync`는 실패 시 **같은 prompt를 그대로 재시도**한다. 이전 시도의 exit code, stderr, errorMessages 어느 것도 다음 prompt에 포함되지 않는다.

```csharp
for (var attempt = 1; attempt <= maxRetries; attempt++)
{
    // 매번 동일한 prompt
    var result = await RunStreamAsync(prompt, model, workingDirectory, logger, output, ct);
    if (result.Success) return result;
    // 실패해도 prompt 갱신 없음
}
```

이러면 retry가 사실상 "한 번 더 운빨로 시도"이지 학습 retry가 아니다. **`improvement.md`의 P0-2(Verification Gate)와 직결되는 문제로, verification 추가 전에 이 retry feedback loop부터 고치는 게 선결 조건이다.**

### 제안

`RunWithRetryAsync`를 `RunWithFeedbackRetryAsync`로 확장:

```csharp
public async Task<ClaudeResult> RunWithFeedbackRetryAsync(
    string basePrompt,
    Func<ClaudeResult, string?>? buildRetryContext = null,
    ...)
{
    string currentPrompt = basePrompt;
    for (var attempt = 1; attempt <= maxRetries; attempt++)
    {
        var result = await RunStreamAsync(currentPrompt, ...);
        if (result.Success) return result;

        // 실패 컨텍스트를 다음 prompt에 prepend
        var retryContext = buildRetryContext?.Invoke(result) ?? DefaultFailureContext(result);
        currentPrompt = $"""
            {retryContext}

            ---

            {basePrompt}
            """;
    }
}
```

기본 `DefaultFailureContext`:

```
이전 시도가 실패했습니다.
Exit code: {exitCode}
Stderr: {stderr}
Error messages: {errorMessages}

원인을 분석하고 다른 접근으로 해결하세요.
```

### 영향

- 같은 retry 횟수에서 성공률 상승
- 추후 verification gate가 추가될 때 검증 실패 출력을 그대로 `buildRetryContext`로 주입 가능 → Verification Gate의 핵심 인프라

---

## P0-3. `BuildPrompt`가 너무 빈약함

### 문제

`Ralph/Services/ParallelExecutor.cs:510` 병렬 worktree에서 도는 Claude가 받는 prompt 전체:

```csharp
return $"""
    Task ID: {task.Id}
    Task: {task.Title}
    {task.Prompt}
    참고: {_tasksFile} 파일에서 apiSpecs, samplePages 등 추가 정보를 확인할 수 있습니다.
    완료 후 생성된 파일 목록을 알려주세요.
    """;
```

**누락된 결정적 정보:**

1. 같은 batch에서 동시에 도는 다른 task가 무엇인지 (충돌 회피용)
2. 자기 task의 `modifiedFiles` 경계 — "이 파일 외에는 만지지 마라"
3. `outputFiles` 기대치
4. 의존하던 task(`dependsOn`)의 산출물 위치/요약
5. `tasks.json`은 절대 수정 금지 (worktree 격리상 stale)
6. 민감 파일 목록(.env, *.key, ...)은 절대 commit 금지

### 제안

```csharp
private string BuildPrompt(TaskItem task, BatchContext? batchCtx = null)
{
    var sb = new StringBuilder();
    sb.AppendLine($"Task ID: {task.Id}");
    sb.AppendLine($"Title: {task.Title}");
    sb.AppendLine($"Category: {task.Category ?? "-"} | Phase: {task.Phase ?? "-"}");
    sb.AppendLine();
    sb.AppendLine("## Scope (엄격히 준수)");

    if (task.ModifiedFiles is { Count: > 0 })
    {
        sb.AppendLine("이 태스크에서 수정/생성 가능한 파일:");
        foreach (var f in task.ModifiedFiles) sb.AppendLine($"  - {f}");
        sb.AppendLine("위 목록 외의 파일은 절대 수정하지 마라. " +
                      "필요 시 작업을 중단하고 보고하라.");
    }

    if (task.OutputFiles is { Count: > 0 })
    {
        sb.AppendLine("이 태스크가 생성해야 할 산출물:");
        foreach (var f in task.OutputFiles) sb.AppendLine($"  - {f}");
    }

    sb.AppendLine();
    sb.AppendLine("## 절대 금지");
    sb.AppendLine("- tasks.json 수정 금지 (worktree 격리 환경, 변경 시 머지 충돌)");
    sb.AppendLine("- 민감 파일(.env, *.pem, *.key, credentials.json 등) 생성/커밋 금지");
    sb.AppendLine("- 위 Scope 외 파일 변경 금지");

    if (batchCtx?.SiblingTasks is { Count: > 0 })
    {
        sb.AppendLine();
        sb.AppendLine("## 동시 실행 중인 다른 태스크 (참고)");
        foreach (var s in batchCtx.SiblingTasks)
            sb.AppendLine($"  - {s.Id}: {s.Title} (modifiedFiles: {string.Join(", ", s.ModifiedFiles ?? [])})");
        sb.AppendLine("위 태스크들과 병렬로 worktree에서 실행 중이다. " +
                      "본 태스크는 자신의 modifiedFiles 경계만 지키면 충돌이 없도록 설계되었다.");
    }

    sb.AppendLine();
    sb.AppendLine("## 작업 지시");
    sb.AppendLine(task.Prompt);

    sb.AppendLine();
    sb.AppendLine("## 완료 보고");
    sb.AppendLine("- 실제로 생성/수정한 파일 목록 (전체 경로)");
    sb.AppendLine("- Scope 위반이 있었다면 그 사유와 어떤 파일을 건드렸는지");

    return sb.ToString();
}
```

### 영향

- `modifiedFiles` 경계를 prompt에 명시만 해도 P3의 `modifiedFiles` 검증에서 잡힐 위반이 사전에 줄어듦
- tasks.json 동기화 문제(P0-5) 예방
- 30분~1시간 작업, 큰 효과

---

## P0-4. `--task <id>`가 의존성 검사 없이 실행됨

### 문제

`Program.cs`의 단일 task 실행 경로(`--task`)는 `dependsOn`이 done이 아닌 상태에서도 강행한다. 디버깅용으로 의도된 경우도 있지만, **무경고 강행은 sliently broken state를 만든다.**

### 제안

```bash
ralph --task <id>          # dependsOn 미완료 시 경고 + 확인 prompt
ralph --task <id> --force  # 의존성 무시 강행 (명시적)
```

기본은 의존성 검사 후 경고:

```
⚠️  태스크 'auth-impl'의 의존성이 완료되지 않았습니다:
   - auth-plan: pending
계속하시겠습니까? [y/N]
```

`--force` 시에만 무시.

---

## P0-5. tasks.json 동기화 — worktree 안에서 수정 방어

### 문제

`MarkTaskDoneThreadSafe`의 `_taskFileLock`은 ralph 프로세스 내 락이다. 그런데 worktree에서 도는 Claude Code는 자기 worktree에 있는 (분기 시점의 stale한) `tasks.json`을 보거나 쓸 수 있다. 사용자 prompt가 "tasks.json을 업데이트하라"고 지시하면:

1. 각 worktree마다 다른 버전의 tasks.json이 생김
2. 머지 시 거의 확실한 충돌
3. base의 ralph가 갱신한 done 상태가 worktree의 옛 tasks.json으로 덮어씌워질 위험

### 제안

1. **Prompt 차원 (즉시)** — P0-3의 BuildPrompt에 "tasks.json은 절대 수정 금지" 명시 (이미 P0-3에 포함)
2. **검증 차원** — worktree 종료 시 `git diff --name-only`에 `tasks.json`이 포함되어 있으면 즉시 강제 revert + 경고 로그
3. **머지 차원** — worktree 머지 직전 `tasks.json`은 ours 전략 강제 (base의 상태가 source of truth)

### 영향

기존 `CommitTasksFileAsync`로 base는 보호되지만 worktree 측 위반이 silent로 누적될 수 있다. 위 3중 방어로 격리된 가드 확보.

---

## P0-6. 4-phase 강제 — 단순 PRD 시 낭비

### 문제

`Ralph/Services/PlanGenerator.cs:163`이 모든 feature를 plan/impl/test/commit 4 task로 강제한다. PRD가 "README 한 줄 고쳐달라" 수준이어도 4 task가 만들어진다. 비용 4배, 시간 4배.

### 제안

PlanPrompt에 분기 가이드 추가:

```
## Task Decomposition Guidance

Choose the appropriate granularity per feature:

- **Trivial change** (single-file edit, doc tweak, version bump):
    1 task, category: "implementation". No plan/test/commit split.

- **Small feature** (1-3 files, no new architecture):
    2 tasks: implementation + commit. Skip plan/test if PRD가 충분히 구체적.

- **Standard feature** (multiple files, new module):
    Full 4-phase: plan → impl → test → commit.

- **Complex feature** (cross-cutting, schema change):
    Consider splitting into multiple sub-features, each 4-phase.

Default to the smallest split that still ensures quality. PRD가 명시적으로
"각 기능을 plan/impl/test/commit으로 분해"라고 요구하지 않는 한
4-phase를 강제하지 마라.
```

`PlanGenerator`의 4-phase mismatch 경고도 "trivial/small feature 의도였는지" 판단 후 경고 여부 결정하도록 수정.

### 영향

- 단순 PRD에서 token/시간 비용 최대 75% 절감
- 사용자 만족도 (대규모 PRD에는 영향 없음)

---

## P1-1. Cost / Token 추적 — stream-json에 이미 다 있음

### 문제

`improvement.md`의 P0-3은 cost tracking을 큰 기능으로 다루지만, **사실 데이터는 이미 stream-json에 흐르고 있고 ralph가 그냥 무시하고 있다.**

`Ralph/Services/ClaudeService.cs:170` 근처에서 stream-json을 line-by-line으로 파싱 중이다. Claude Code의 stream-json은 마지막 `result` 메시지에 `usage`(input_tokens, output_tokens, cache_read_input_tokens, cache_creation_input_tokens)를 포함한다. 현재 코드는 `result` 메시지의 `result` 텍스트 필드만 읽고 `usage`는 버린다.

### 제안 (Quick Win)

1. `ClaudeResult`에 다음 필드 추가:

   ```csharp
   public class ClaudeResult
   {
       public bool Success { get; init; }
       public string Output { get; init; } = "";
       // 신규
       public TokenUsage? Usage { get; init; }
       public TimeSpan Duration { get; init; }
       // ...
   }

   public record TokenUsage(
       long InputTokens, long OutputTokens,
       long CacheReadTokens, long CacheCreationTokens);
   ```

2. `RunStreamAsync`에서 `result` 메시지의 `usage` 객체 파싱

3. `Services/CostTracker.cs` 신규:

   ```csharp
   public class CostTracker
   {
       private readonly string _logFile = ".ralph-logs/cost.json";

       public async Task RecordAsync(string taskId, ClaudeResult result, string model)
       {
           // append-only JSON Lines로 기록 (atomic)
           var entry = new {
               taskId, timestamp = DateTime.UtcNow,
               model,
               inputTokens = result.Usage?.InputTokens ?? 0,
               outputTokens = result.Usage?.OutputTokens ?? 0,
               cacheReadTokens = result.Usage?.CacheReadTokens ?? 0,
               estimatedUsd = Estimate(model, result.Usage),
               durationSec = result.Duration.TotalSeconds,
           };
           await File.AppendAllTextAsync(_logFile,
               JsonSerializer.Serialize(entry) + "\n");
       }
   }
   ```

4. `ralph --cost` 명령:

   ```
   Session 20260428-103211
     Total: 1.2M input, 380K output, $12.45 (estimated)
     By task:
       auth-impl     | 8.2K in, 2.1K out | 47s | $0.09
       payment-impl  | 7.9K in, 2.0K out | 44s | $0.08
       ...
   ```

### 영향

- **반나절 작업.** Budget gate(`improvement.md` P0-3 후속)는 이 위에 얹는 별개 작업.
- 사용자가 "내가 얼마나 썼지?"를 즉시 답할 수 있음.

---

## P1-2. Prompt Preview / Inspect

### 문제

Ralph의 결과 품질은 100% prompt 품질에 달려있지만, **사용자가 실제로 Claude에 보낼 prompt를 미리 볼 방법이 없다.** `--dry-run`은 task 목록만, `--prompts`는 task 객체의 `prompt` 필드만 보여준다. P0-3에서 BuildPrompt를 강화하면 더 중요해진다.

### 제안

```bash
ralph --show-prompt <taskId>           # BuildPrompt 결과 출력
ralph --dry-run --show-prompts         # 모든 task의 fullPrompt 출력
```

선택적으로:

```bash
ralph --edit-prompt <taskId>           # $EDITOR로 prompt 편집 후 tasks.json 갱신
```

### 영향

- 디버깅 시간 대폭 단축
- Plan generator가 만든 prompt 품질을 사람이 검토 가능

---

## P1-3. Plan Validation 강화 — 실행 전에 잡기

### 문제

현재 `PlanGenerator`는 4-phase 카운트 mismatch 경고만 한다. DAG cycle, modifiedFiles overlap, dependsOn 무결성, prompt-카테고리 정합성은 **실행 시점**에야 발견된다. 폭발이 늦다.

### 제안

`Services/PlanValidator.cs` 신규. `PlanGenerator` 직후 + `--run` 시작 직전에 실행:

```csharp
public class PlanValidationReport
{
    public List<string> Cycles { get; set; } = [];
    public List<(string TaskA, string TaskB, List<string> Files)> FileOverlaps { get; set; } = [];
    public List<(string Task, string MissingDep)> BrokenDeps { get; set; } = [];
    public List<(string Task, string Issue)> CategoryMismatches { get; set; } = [];
    public bool HasErrors => Cycles.Count > 0 || BrokenDeps.Count > 0;
    public bool HasWarnings => FileOverlaps.Count > 0 || CategoryMismatches.Count > 0;
}
```

검사 항목:

1. **DAG cycle** — `TaskManager.HasCycle` 재사용. cycle 있으면 error.
2. **dependsOn 참조 무결성** — 존재하지 않는 task ID 참조 시 error.
3. **modifiedFiles overlap** — 같은 batch(서로 의존 없음)에서 같은 파일을 수정하는 task 쌍 검출. 병렬 머지 시 충돌 사전 경고.
4. **prompt-category 정합성** — `category: "test"`인데 prompt에 "구현하라"가 있거나, `category: "commit"`인데 새 코드 생성을 요구하면 경고.
5. **modifiedFiles 누락 의심** — prompt에 `*.py`, `tests/`, `src/foo.ts` 같은 명시적 파일 언급이 있는데 `modifiedFiles`에 없으면 경고.

`ralph --validate <file>` 명령으로 단독 실행도 가능하게.

### 영향

- 24개 task 실행 도중 14번째에서 폭발하는 일이 줄어듦
- modifiedFiles overlap 경고는 P3의 modifiedFiles 검증과 합쳐지면 안전망 2중화

---

## P2-1. Pre-built Binaries — 즉시 1순위

`improvement.md`의 P2-1과 동일하지만 **순위를 즉시 1번으로 끌어올림**.

### 이유

- 현재 진입장벽: .NET 8 SDK 설치 → clone → `dotnet publish` → RID 선택 → PATH 설정
- 사용자가 없으면 다른 P0/P1 개선의 피드백 루프 자체가 없다
- GitHub Actions release workflow는 반나절 작업

### 구현

`improvement.md` P2-1의 matrix workflow 그대로. 추가로:

- `install.sh` universal installer (OS/arch 감지 → 적절한 binary download → `~/.local/bin/ralph`)
- `brew tap` 또는 `winget` 패키지는 binary 안정화 후 v1.x에서

---

## P2-2. Webhook / Notification — 진짜 P1

`improvement.md`의 P2-4 끝에 묻혀있는데, **6시간 AFK 끝에 알림이 없다는 건 실제 고통점**이다. 그리고 구현이 단순하다.

### 제안

```json
{
  "workflow": {
    "notifications": {
      "onComplete": "https://hooks.slack.com/services/...",
      "onFailure": "https://hooks.slack.com/services/...",
      "onBudgetReached": "..."
    }
  }
}
```

또는 환경 변수:

```bash
export RALPH_WEBHOOK_URL=https://...
ralph --run
```

POST body는 단순 JSON:

```json
{
  "event": "session_complete",
  "session": "20260428-103211",
  "totalTasks": 24,
  "completed": 24,
  "failed": 0,
  "durationSec": 5832,
  "estimatedCostUsd": 12.45
}
```

curl 한 번 호출. 1시간 작업.

---

## P2-3. 로그 Rotation

`.ralph-logs/`가 무한정 쌓인다. 30일 이상 된 파일 자동 삭제 옵션:

```json
{
  "workflow": {
    "logRetentionDays": 30
  }
}
```

또는 `ralph --logs --cleanup`. 30분 작업.

---

## P3. `improvement.md`의 큰 기능들 (P0~P2 정리 후)

P0~P2가 정리되면 그 위에서 다음을 진행한다. 우선순위는 `improvement.md`의 권장보다 다음 순으로 재배치:

1. **modifiedFiles 검증 (warn 모드)** (`improvement.md` P1-1) — P1-3의 plan validation과 결합
2. **Verification Gate** (`improvement.md` P0-2) — P0-2(retry feedback) 위에 얹음
3. **Crash Recovery (atomic write + heartbeat)** (`improvement.md` P1-4)
4. **Budget Gate** (`improvement.md` P0-3 후반부) — P1-1(cost tracking) 위에 얹음
5. **Learning Loop** (`improvement.md` P0-1) — 데이터 보고 결정. P3에서도 나중. ROI가 long-running loop와 다름.
6. **Sandbox README 경고만 즉시**, Docker sandbox는 사용자 요청 누적 후 (`improvement.md` P1-3)
7. **Plan Iteration UI** (`improvement.md` P2-2)
8. **README 영문화 / Topics** (`improvement.md` P2-3)

---

## 권장 구현 순서 요약

| # | 항목 | 예상 시간 | 임팩트 |
|---|------|-----------|--------|
| 1 | P0-1 Conflict 해결 working dir 버그 | 0.5일 | claude strategy 사용 가능화 |
| 2 | P0-2 Retry feedback loop | 0.5일 | Verification Gate 선결조건 |
| 3 | P0-3 BuildPrompt 강화 | 0.5일 | 결과 품질 + scope 안전성 |
| 4 | P0-4 `--task --force` 분리 | 0.2일 | silent 깨짐 방지 |
| 5 | P0-5 tasks.json worktree 보호 | 0.3일 | 머지 안정성 |
| 6 | P0-6 4-phase 강제 완화 | 0.3일 | 단순 PRD 비용 절감 |
| 7 | P1-1 Cost tracking | 0.5일 | 즉시 가시성 |
| 8 | P1-2 Prompt preview | 0.3일 | 디버깅 시간 단축 |
| 9 | P1-3 Plan validation | 1일 | 실행 전 폭발 차단 |
| 10 | P2-1 Pre-built binaries | 0.5일 | 채택률 |
| 11 | P2-2 Webhook | 0.3일 | AFK UX |
| 12 | P2-3 Log rotation | 0.2일 | 디스크 |
| --- | --- | --- | --- |
| **소계** | **P0~P2 전부** | **~5일** | v0.8 릴리스 가능 |
| 13~ | P3 큰 기능들 | 각 2~5일 | v0.9, v1.0 |

**P0~P2를 5일 안에 v0.8로 묶어 릴리스 → P3는 사용자 피드백 받으면서 진행**을 권장한다.

---

## `improvement.md`와의 차이 요약

| 측면 | improvement.md | 본 문서 |
|------|----------------|---------|
| 출발점 | "정통 Ralph 패턴 충실도" | "현재 코드 무결성 + 사용자 확보" |
| Learning Loop | P0 | P3 (데이터 보고 결정) |
| Pre-built Binary | P2 | P2 즉시 1순위 |
| Cost Tracking | P0 큰 기능 | P1 Quick Win (이미 stream-json에 데이터 있음) |
| Verification Gate | P0 | P3, 단 retry feedback(P0-2) 선결 |
| 코드 버그 수정 | 미언급 | P0-1 ~ P0-6 신설 |
| Webhook | P2-4 잡항 | P2-2 독립 항목 |
| Prompt preview | 미언급 | P1-2 |
| Plan validation | 미언급 | P1-3 |

본 문서는 `improvement.md`를 대체하는 게 아니라 **그 앞에 와야 할 정비 단계**를 명시한다. P0~P2 완료 후에는 `improvement.md`의 P3 항목들이 안정적인 토대 위에서 구현될 수 있다.
