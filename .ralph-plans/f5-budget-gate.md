# F5 계획: `--budget-usd` 임계값 게이트 (P1)

## 1. 배경 / 문제 정의

`CostTracker`(`Ralph/Services/CostTracker.cs`)는 매 Claude 호출마다 추정 USD를
`.ralph-logs/cost.jsonl`에 누적 기록하지만, 누적치를 **실행 도중** 활용하는
경로가 없다. 운영자는 다음과 같은 사고를 피할 수단이 없다:

- 무인 장기 실행(`ralph --run`) 중 비용이 예산을 넘어도 끝까지 돌아간다.
- `MAX_RETRIES`/재시도 루프와 결합해 일시적 비용 폭증이 발생할 수 있다.
- 실행 후 `--cost`로 사후 확인은 가능하지만 손실은 이미 발생한 뒤다.

목적: **각 새 task 시작 직전에 `cost.jsonl` 합산을 측정해, 임계값(USD)
도달 시 새 task를 시작하지 않고 진행 중 task만 완료시키는 soft-gate를 둔다.**
강제 취소는 하지 않는다(중간 데이터 손상 방지).

요구사항 요약:
- CLI: `ralph --run --budget-usd <amount>`
- env: `RALPH_BUDGET_USD` (CLI 미지정 시 fallback)
- 80% 도달 시 1회만 경고
- 100% 도달 시 새 dispatch 차단 + 진행 중 완료까지 대기 + "budget reached"
  메시지 + 다음 실행 안내 + **종료 코드 2**
- 순차 경로(`RunAutoLoop`)에도 동일 적용
- Webhook 알림(선택): `NotificationService`가 이미 있으므로 활용

## 2. 현재 구현 분석

### 2.1 CostTracker 누적 합산 — 이미 부분 구현됨

`Ralph/Services/CostTracker.cs:96-125` `PrintSummaryAsync`가 `cost.jsonl` 전체를
파싱해 `entries.Sum(e => e.EstimatedUsd)`를 출력한다. 동일 로직의 일부가
`Ralph/Program.cs:320-344` `ReadCostSummaryAsync`에도 별도로 존재한다(세션 종료
알림 payload용, JsonDocument로 파싱). **이 둘을 `GetTotalUsdAsync`로
통합·재사용**하는 것이 F5의 핵심 변경 중 하나다.

비용 단가는 `EstimateUsd`(`CostTracker.cs:70-79`)가 USD를 `double`로 반환한다.
F5 요구사항에는 `decimal` 표현이 등장하지만(태스크 prompt) 기존 모델(`CostEntry.EstimatedUsd:
double`, `Program.cs:343 total += usd.GetDouble()`)과의 일관성을 위해 본 PR은
`double`로 통일한다(§5 회귀 위험에서 정밀도 논의).

### 2.2 task dispatch 지점

**병렬 경로** (`Ralph/Services/ParallelExecutor.cs:35-124` `RunAsync`):

```csharp
while (true)
{
    ct.ThrowIfCancellationRequested();

    var readyTasks = _taskManager.GetAllReadyTasks();
    if (readyTasks.Count == 0) { /* done or blocked */ break; }

    if (readyTasks.Count == 1)
        await RunSingleTaskAsync(readyTasks[0], ct);   // ← 새 task 시작
    else
    {
        var batch = batches[0].Take(maxConcurrent).ToList();
        if (batch.Count == 1)
            await RunSingleTaskAsync(batch[0], ct);     // ← 새 task 시작
        else
            await RunParallelBatchAsync(batch, baseBranch, ct); // ← 새 batch 시작
    }
}
```

`RunParallelBatchAsync`(`ParallelExecutor.cs:175-351`)는 batch 내 모든 task를
`Task.WhenAll`로 동시 시작한다. **batch 내부의 개별 task 시작 직전 게이팅은
의미가 없다**(같은 instant에 모두 시작). 따라서 게이트는 **batch 단위**로
건다 — `RunAsync`의 while 루프 매 iteration 시작점.

**순차 경로** (`Ralph/Program.cs:991-1034` `RunAutoLoop`):

```csharp
while (true)
{
    ct.ThrowIfCancellationRequested();
    var nextId = tm.GetNextReadyTask();
    if (nextId == null) break;

    var exitCode = await RunTaskAuto(...);  // ← 새 task 시작
    if (exitCode == 2) continue;            // dependency-blocked
    if (exitCode != 0) break;
}
```

여기서도 게이트는 `RunTaskAuto` 호출 직전.

`RunTaskAuto`의 반환값 `2`는 이미 "dependency blocked"로 사용 중이다(`Program.cs:899`).
**budget 게이트의 종료 코드 2와 의미가 충돌하지 않도록**, 게이트는
`RunAutoLoop` 자체가 직접 처리하고 `RunTaskAuto`는 호출하지 않는다.
즉 `RunAutoLoop`의 반환값을 `int`로 유지하되, budget 차단 시 별도 분기로
`return 2`를 추가한다(현재는 항상 0 반환).

### 2.3 Webhook (NotificationService)

`Ralph/Services/NotificationService.cs:19-71` `NotifyAsync`는 세션 종료
시점에 `event: session_complete | session_failed` payload를 1회 POST 한다.
호출은 `Ralph/Program.cs:290-315`에서 finally 형태로 통합되어 있어
**budget 차단 종료도 이 흐름을 그대로 통과한다**(success=false 분기).

별도 "budget_reached" 이벤트를 새로 만들기보다 기존 payload에
`terminationReason` 필드(선택)를 덧붙이고, success=false로 분기시켜
`onFailure`/`onComplete` 우선순위 로직을 그대로 활용한다(§3.6 상세).

### 2.4 CLI/env 파서 패턴

`Ralph/Program.cs:39-78`의 패턴은 두 부류:

```csharp
// boolean
var forceFlag = argList.Remove("--force");
var envStrictFiles = Environment.GetEnvironmentVariable("RALPH_STRICT_FILES")?.ToLower() == "true";
var strictFiles = argList.Remove("--strict-files") || envStrictFiles;

// value-bearing
var maxParallelArg = 0;
var maxParallelIdx = argList.IndexOf("--max-parallel");
if (maxParallelIdx >= 0 && maxParallelIdx + 1 < argList.Count)
{
    int.TryParse(argList[maxParallelIdx + 1], out maxParallelArg);
    argList.RemoveRange(maxParallelIdx, 2);
}
```

`--budget-usd <amount>`는 후자 패턴을 따른다. 단 `int.TryParse`가 아니라
`double.TryParse(... NumberStyles.Float, CultureInfo.InvariantCulture, ...)`로
소수점 입력(`0.5`, `0.001`)을 받아야 한다.

### 2.5 ParallelExecutor 생성자

`ParallelExecutor.cs:19-33` 생성자는 이미 `bool strictFiles = false` 옵션을
받는다(F4). `double? budgetUsd = null`을 같은 패턴으로 추가한다. 호출부는
`Program.cs:276-278` 한 곳뿐.

## 3. 설계

### 3.1 신규 메서드: `CostTracker.GetTotalUsdAsync`

**시그니처:**

```csharp
public async Task<double> GetTotalUsdAsync(CancellationToken ct = default);
```

- 인스턴스 메서드(생성자 인자 없음, 인스턴스화 비용 미미하므로 기존
  `CostTracker` 호출자 패턴 답습 — `Program.cs:528-531`, `ParallelExecutor.cs:32`).
- 반환 단위: USD (double).
- `LogFilePath`(`CostTracker.cs:44`) 부재 시 `0.0` 반환(파일 없음 = 비용 없음).
- 한 줄씩 `JsonSerializer.Deserialize<CostEntry>`로 읽고
  `EstimatedUsd`만 누적. `JsonException` 발생 라인은 skip(`PrintSummaryAsync`와
  동일 정책, `CostTracker.cs:111`).
- 빈 줄도 skip.

**구현 스케치:**

```csharp
public async Task<double> GetTotalUsdAsync(CancellationToken ct = default)
{
    if (!File.Exists(LogFilePath)) return 0.0;

    var total = 0.0;
    await foreach (var line in File.ReadLinesAsync(LogFilePath, ct))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        try
        {
            var entry = JsonSerializer.Deserialize<CostEntry>(line, JsonOpts);
            if (entry != null) total += entry.EstimatedUsd;
        }
        catch (JsonException) { /* skip malformed line */ }
    }
    return total;
}
```

**기존 `Program.cs:320-344` `ReadCostSummaryAsync`는 제거**하고 호출부를
`new CostTracker().GetTotalUsdAsync(...)`로 교체한다. 합산 로직 단일 출처화.

**왜 `decimal`이 아닌 `double`인가:** 모든 기존 비용 데이터(`CostEntry.EstimatedUsd`,
`EstimateUsd` 반환, `cost.jsonl` 직렬화 형식)가 `double`이며, 임계값 비교는
부동소수점 오차가 의미를 갖지 않을 정도의 크기 차(USD 단위 vs 임계값) 다.
`decimal` 도입은 모든 신호 경로 마이그레이션을 동반해 본 PR scope를 벗어난다.

### 3.2 ParallelExecutor 변경

#### 3.2.1 생성자/필드

```csharp
public ParallelExecutor(
    TaskManager taskManager, ClaudeService claude, GitService git,
    WorktreeService worktree, RalphLogger logger, string tasksFile,
    string? model = null, bool strictFiles = false,
    double? budgetUsd = null)              // ← 추가
{
    ...
    _budgetUsd = budgetUsd;
}

private readonly double? _budgetUsd;
private bool _budgetWarningEmitted;        // 80% 경고 1회만
private bool _budgetReached;               // 100% 도달 (호출자가 종료 코드 2로 변환)
```

`_budgetReached`는 `RunAsync`의 정상 종료 후에도 호출자(`Program.HandleRun`)가
종료 코드를 결정하기 위해 읽는 신호다. `public bool BudgetReached => _budgetReached;`
프로퍼티로 노출.

#### 3.2.2 게이트 로직 — `CheckBudgetAsync`

```csharp
/// <summary>
/// 새 task/batch dispatch 직전에 호출. 반환값:
///   true  → 진행 가능
///   false → budget reached, 새 dispatch 중단 (호출자가 break 후 종료 코드 2로 변환)
/// </summary>
private async Task<bool> CheckBudgetAsync(CancellationToken ct)
{
    if (_budgetUsd is not { } budget || budget <= 0.0) return true;

    var total = await _cost.GetTotalUsdAsync(ct);

    // 80% 1회 경고
    if (!_budgetWarningEmitted && total >= budget * 0.8)
    {
        _budgetWarningEmitted = true;
        var pct = total / budget * 100.0;
        AnsiConsole.MarkupLine(
            $"[yellow]⚠ 예산 80% 도달[/] (${total:F2} / ${budget:F2}, {pct:F0}%)");
        _logger.Warn($"[budget] 80% threshold hit: ${total:F4} / ${budget:F4}");
    }

    // 100% 차단
    if (total >= budget)
    {
        _budgetReached = true;
        AnsiConsole.MarkupLine(
            $"[red]✗ budget reached[/] (${total:F2} / ${budget:F2}). " +
            "새 태스크 시작을 중단합니다. 진행 중 태스크는 완료까지 대기합니다.");
        AnsiConsole.MarkupLine(
            "[dim]다음 실행: 예산을 늘리거나(--budget-usd <larger>) " +
            "예산 없이(`ralph --run`) 재개 가능합니다.[/]");
        _logger.Error($"[budget] reached: ${total:F4} / ${budget:F4}");
        return false;
    }

    return true;
}
```

`budget <= 0.0`을 `null`과 동격으로 취급해 `--budget-usd 0`이나 음수 입력이
무한 차단을 일으키지 않도록 한다(요구사항 §1의 "미설정 시 기존 동작 보존"
연장선).

#### 3.2.3 `RunAsync` 게이트 삽입 위치

```csharp
while (true)
{
    ct.ThrowIfCancellationRequested();

    // F5: budget 게이트 — readyTasks 조회 전에 검사
    if (!await CheckBudgetAsync(ct)) break;

    var readyTasks = _taskManager.GetAllReadyTasks();
    if (readyTasks.Count == 0) { ... break; }

    // 기존 분기 (단일/병렬 batch 시작) ...
}
```

while 매 iteration 시작점이 "다음 task/batch dispatch 직전"이므로 단일 게이트
호출만으로 모든 분기를 커버. `_budgetReached=true`이면 break으로 루프 탈출,
이후 `_worktree.CleanupAllAsync` → `return 0`(정상 흐름).

**왜 `RunParallelBatchAsync` 내부의 batch 시작 직전이 아닌 바깥인가:**
batch 내부 task들은 `Task.WhenAll`로 동시 시작되므로 batch 단위 게이트만
의미를 가진다. 또 `RunParallelBatchAsync`는 worktree 생성·머지 흐름의
원자적 단위라 중간에 끼어드는 게이트를 더 두면 cleanup 분기가 복잡해진다.

#### 3.2.4 종료 코드 분기

`RunAsync` 자체는 0/1만 반환(기존 시그니처 유지). 호출자
`HandleRun`(`Program.cs`)이 `executor.BudgetReached`를 확인해 종료 코드 2를
결정. 이렇게 하면 기존 실패 코드(1)와 budget 코드(2)를 깔끔히 구분.

### 3.3 RunAutoLoop(순차 경로) 변경

`Program.cs:985-1034` 시그니처/구조를 유지하되 다음을 추가:

```csharp
async Task<int> RunAutoLoop(
    TaskManager tm, ClaudeService claude, GitService git, RalphLogger logger,
    bool dryRun, bool commitOnComplete, string? model,
    double? budgetUsd, CancellationToken ct)        // ← budgetUsd 추가
{
    ShowProgress(tm, logger);

    var cost = new CostTracker();
    var budgetWarned = false;

    while (true)
    {
        ct.ThrowIfCancellationRequested();

        // F5: budget 게이트
        if (budgetUsd is { } b && b > 0.0)
        {
            var total = await cost.GetTotalUsdAsync(ct);
            if (!budgetWarned && total >= b * 0.8)
            {
                budgetWarned = true;
                AnsiConsole.MarkupLine(
                    $"[yellow]⚠ 예산 80% 도달[/] (${total:F2} / ${b:F2}, " +
                    $"{(total / b * 100):F0}%)");
                logger.Warn($"[budget] 80% threshold hit: ${total:F4} / ${b:F4}");
            }
            if (total >= b)
            {
                AnsiConsole.MarkupLine(
                    $"[red]✗ budget reached[/] (${total:F2} / ${b:F2}). 새 태스크 시작 중단.");
                AnsiConsole.MarkupLine(
                    "[dim]다음 실행: 예산을 늘리거나 예산 없이 재개 가능합니다.[/]");
                logger.Error($"[budget] reached: ${total:F4} / ${b:F4}");
                return 2;                              // ← 종료 코드 2
            }
        }

        var nextId = tm.GetNextReadyTask();
        if (nextId == null) { /* 기존 종료 분기 */ break; }

        var exitCode = await RunTaskAuto(...);
        if (exitCode == 2) continue;     // 기존 의미: dependency blocked
        if (exitCode != 0) { break; }
    }
    return 0;
}
```

**`RunTaskAuto`의 기존 `return 2`(dependency blocked)와의 충돌 회피:**
`RunAutoLoop`은 `RunTaskAuto` 반환 2를 `continue`로 흡수해 외부에 노출하지
않는다. 본 PR이 새로 추가하는 `return 2`는 `RunAutoLoop` 자체의 직접 반환이며
해당 분기가 일찍 발생(루프 시작점). 따라서 `HandleRun`에서 본 `RunAutoLoop`의
2는 항상 budget을 의미.

호출부 `Program.cs:285`:

```csharp
exitCode = await RunAutoLoop(tm, claude, git, logger,
    dryRun: false, commitOnComplete: true, modelArg, budgetUsd, cts.Token);
```

dry-run/interactive 경로는 budget 게이트를 적용하지 않는다(§3.7).

### 3.4 CLI/env 우선순위 — `Program.cs`

`--budget-usd <amount>` + `RALPH_BUDGET_USD`. CLI 우선, CLI 미지정 시 env,
둘 다 미지정/파싱 실패 시 `null`(미설정 = 기존 동작).

```csharp
// 환경변수 (Program.cs:27-32 인근)
var envBudgetRaw = Environment.GetEnvironmentVariable("RALPH_BUDGET_USD");
double? envBudget = double.TryParse(envBudgetRaw,
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var eb)
    ? eb
    : (double?)null;

// CLI (Program.cs:39-50 인근)
double? cliBudget = null;
var budgetIdx = argList.IndexOf("--budget-usd");
if (budgetIdx >= 0 && budgetIdx + 1 < argList.Count)
{
    if (double.TryParse(argList[budgetIdx + 1],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var bv))
    {
        cliBudget = bv;
    }
    else
    {
        AnsiConsole.MarkupLine(
            $"[red]Error: --budget-usd 값을 파싱할 수 없습니다: '{Markup.Escape(argList[budgetIdx + 1])}'[/]");
        return 1;
    }
    argList.RemoveRange(budgetIdx, 2);
}

// 우선순위
double? budgetUsd = cliBudget ?? envBudget;
```

InvariantCulture 고정으로 ko-KR locale의 `,`/`.` 차이를 회피.

음수/0은 `CheckBudgetAsync`/`RunAutoLoop`에서 "미설정 동격" 처리(§3.2.2).

### 3.5 종료 코드 2 반환 흐름 — `HandleRun`

`Program.cs:268-318` 수정 포인트:

```csharp
if (useParallel)
{
    var executor = new ParallelExecutor(
        tm, claude, git, worktree, logger, tasksFile, modelArg,
        strictFiles: strictFiles, budgetUsd: budgetUsd);   // ← 추가
    exitCode = await executor.RunAsync(concurrency, cts.Token);
    if (exitCode == 0 && executor.BudgetReached) exitCode = 2;
}
else
{
    exitCode = await RunAutoLoop(tm, claude, git, logger,
        dryRun: false, commitOnComplete: true, modelArg,
        budgetUsd, cts.Token);                             // ← budgetUsd 전달
    // RunAutoLoop이 직접 2를 반환
}
```

이후 알림 블록(`Program.cs:290-315`)은 **변경 없이 그대로 실행**. `success`
판정은 `exitCode == 0` 기준이라 budget 차단 시 success=false → onFailure
webhook(설정된 경우) 발화. `HandleRun`의 최종 `return exitCode`로 종료 코드
2가 main에서 노출.

`Program.cs:114-116`의 `Task.FromResult(ShowUnknown(command))` 등 기타 분기는
손대지 않음.

### 3.6 Webhook 알림 (선택, 후방 호환)

기본은 §3.5 그대로 — `success=false` payload가 onFailure(또는 onComplete)
webhook으로 가므로 webhook을 받는 쪽은 "세션이 실패로 끝남"을 인지한다.

추가 정밀도를 위해 `NotificationService.NotifyAsync`에 옵션 인자
`string? terminationReason = null`을 받고 payload에 `terminationReason`
필드를 덧붙인다(미지정이면 필드 자체 누락 — 기존 수신자 후방 호환). 호출부
(`Program.cs:300-310`)에서 `exitCode == 2 ? "budget_reached" : null` 전달.

```csharp
// NotificationService.cs payload 변경 (선택)
var payload = new
{
    @event = success ? "session_complete" : "session_failed",
    session = sessionId,
    success,
    totalTasks,
    completedTasks,
    failedTasks,
    durationSec,
    estimatedCostUsd,
    terminationReason,        // ← null이면 JsonIgnoreCondition으로 누락 (또는 익명형 필드 그대로 둠)
    host = Environment.MachineName,
    timestamp = DateTime.UtcNow.ToString("o"),
};
```

**선택**으로 둔 이유: scope 외 파일(`NotificationService.cs`) 수정이며,
필드 추가만으로도 후방 호환은 깨지지 않지만 본 PR 핵심은 게이트 로직이다.
구현 PR에서 시간이 남으면 적용, 아니면 P2.

### 3.7 적용 범위(어떤 명령에 게이트가 붙는가)

| 명령 | 게이트 적용 | 근거 |
|---|---|---|
| `ralph --run`(병렬) | ✅ | `ParallelExecutor.RunAsync`의 while 루프 시작점 |
| `ralph --run --sequential` | ✅ | `RunAutoLoop`의 while 루프 시작점 |
| `ralph --task <id>` | ❌ | 단일 태스크 단발 실행. 게이트 의미 없음(처음에 비용 0이면 통과, 1이면 무조건 차단) |
| `ralph --interactive` | ❌ | 사용자가 직접 결정. 자동 게이트 충돌. P2에서 선택 |
| `ralph --dry-run` | ❌ | Claude 호출이 발생하므로 비용은 누적되지만 실행 의도가 "미리보기"라 차단은 부적절. dry-run은 game-day 시나리오로 별도 |
| `ralph --plan` | ❌ | PRD → tasks.json 1회 호출. budget 개념과 결이 다름 |

**결정**: `--budget-usd`는 `--run`에서만 의미를 갖는다. `--budget-usd`가
`--run` 외 명령과 함께 들어오면 조용히 무시(파싱은 하되 사용 안 함)
또는 경고 출력 — 본 PR은 **조용히 무시**(다른 옵션도 동일 패턴, 예:
`--max-parallel`도 `--list`에 무의미하지만 무시).

### 3.8 ShowHelp 및 환경변수 docs

`Program.cs:783-805`에 두 줄 추가:

```
  --budget-usd <amount>   누적 비용이 amount(USD)에 도달하면 새 태스크 시작 차단 (--run only)
```

```
  RALPH_BUDGET_USD            누적 비용 임계값(USD). CLI가 우선
```

## 4. 구현 단계 분해 (구현 PR에서 수행할 작업)

1. `Ralph/Services/CostTracker.cs`:
   - `GetTotalUsdAsync(CancellationToken)` 추가.
2. `Ralph/Services/ParallelExecutor.cs`:
   - 생성자에 `double? budgetUsd = null` 추가, `_budgetUsd`/`_budgetWarningEmitted`/`_budgetReached` 필드.
   - public `BudgetReached` 프로퍼티.
   - `CheckBudgetAsync(CancellationToken)` private 메서드.
   - `RunAsync` while 루프 첫 줄에 `if (!await CheckBudgetAsync(ct)) break;`.
3. `Ralph/Program.cs`:
   - `--budget-usd <amount>` 파싱(InvariantCulture, double).
   - `RALPH_BUDGET_USD` env fallback.
   - `HandleRun`에서 `ParallelExecutor` 생성자 인자 전달, `executor.BudgetReached` 시 `exitCode=2`.
   - `RunAutoLoop` 시그니처에 `double? budgetUsd` 추가, while 루프 시작점에
     게이트 로직 인라인. 차단 시 `return 2`.
   - 기존 `ReadCostSummaryAsync`(`Program.cs:320-344`) 제거 → `new CostTracker().GetTotalUsdAsync(...)` 호출로 대체.
   - `ShowHelp`에 옵션 줄 + 환경변수 줄 추가.
4. (선택) `Ralph/Services/NotificationService.cs`:
   - `NotifyAsync`에 `string? terminationReason = null` 인자 추가, payload 포함.
   - `HandleRun`에서 `exitCode == 2 ? "budget_reached" : null` 전달.

코드 변경은 위 3(또는 4)개 파일에 한정. 다른 파일은 손대지 않는다.

## 5. 회귀 위험 분석

| 위험 | 가능성 | 영향 | 완화책 |
|---|---|---|---|
| `budgetUsd=null` 또는 `≤0` 경로에서 기존 동작이 미세하게 달라짐 | 낮음 | 회귀 가능성 | `CheckBudgetAsync`/`RunAutoLoop`이 `null`/`≤0` 첫 줄에서 `return true`/skip. 비용 파일 read도 발생 안 함(I/O 비용 0). 단위 테스트로 이 경로를 명시 검증 |
| 매 batch마다 `cost.jsonl` 전체 재파싱 → I/O 부담 | 중간 | 큰 cost.jsonl(수천 라인)에서 batch 전 수백 ms 지연 가능 | `cost.jsonl`은 호출당 1라인 추가, 전형적 세션 수십~수백 라인이라 무시 가능. P2에서 in-memory 누적 캐시 도입 검토 |
| double 누적 정밀도 → 임계값 경계에서 1cent 단위 어긋남 | 매우 낮음 | 80% 경고 한 번 늦거나 100% 차단이 한 번 늦음 | budget 단위가 USD(소수 둘째 자리)이고 단가 추정 자체가 ±10% 정확도. 정밀도 손실은 의미 없음. PR 본문에 명시 |
| ko-KR 등 콤마 소수점 locale에서 `--budget-usd 0,5` 입력 실패 | 중간 | 사용자 불편 | InvariantCulture 고정. 실패 시 명시적 에러 메시지로 안내 |
| budget 차단 시 cleanup 누락(병렬 worktree 잔존) | 낮음 | 다음 실행 시 stale worktree | `RunAsync`는 budget으로 break 후에도 finally가 아닌 끝부분 `_worktree.CleanupAllAsync` 호출 경로를 통과. `RunParallelBatchAsync`는 진행 중 batch가 있으면 자체 finally가 cleanup. 별도 정리 코드 불필요. 검증 시나리오에서 worktree 잔존 여부 확인 |
| 진행 중 batch가 있는데 budget 차단되면 batch 완료까지 대기 — 그 사이에 비용이 더 누적됨 | 중간 | 임계값 약간 초과 가능 | 요구사항 그대로의 동작("진행 중은 강제 종료하지 않음"). 운영자에게는 임계값을 약간 보수적으로 설정하라고 PR 본문에 가이드 |
| `RunTaskAuto` 반환 2(dependency blocked)와 budget 코드 2 혼동 | 중간 | 오진단 | `RunAutoLoop`이 `RunTaskAuto`의 2를 `continue`로 흡수, 외부에 노출 안 함. budget 차단의 2는 `RunAutoLoop` 자체에서만 반환. 차이를 코드 주석에 명시 |
| `--budget-usd`를 `--task`/`--interactive`/`--dry-run`에 함께 줘도 동작 안 함 | 낮음 | 사용자 혼란 | 옵션 파서는 모든 명령 공통이라 파싱은 되나 사용 분기에서 무시. ShowHelp에 "(--run only)" 명시 |
| `cost.jsonl` 외부에서 손으로 편집해 비정상 라인 삽입 | 매우 낮음 | 합산 부정확 | `JsonException` skip(기존 `PrintSummaryAsync`와 동일 정책). 영향 범위 1라인 |
| 테스트 시 임시 cost.jsonl 시뮬레이션이 실제 .ralph-logs와 충돌 | 낮음 | 단위 테스트 격리 | `CostTracker`의 `LogFilePath`는 const 기반(상대경로). 테스트는 임시 작업 디렉토리에서 실행하거나 cost.jsonl을 임시로 백업/복원 |
| 80% 경고 플래그가 process 단위라 같은 세션 내 여러 `RunAsync`/`RunAutoLoop` 호출 시 첫 호출만 경고 | 매우 낮음 | 운영상 이슈 거의 없음 | ralph CLI는 단발 process. 동일 process 안에서 `RunAsync` 두 번 호출 경로 없음. 추후 daemon화 시 재고 |
| budget 차단 후 `tasks.json` 일관성 — 진행 중이던 batch가 중간에 멈췄으면 partial completion | 낮음 | 다음 실행이 부분 상태에서 시작 | "진행 중은 끝까지 대기"이므로 partial은 발생 안 함(batch 단위 완전 종료 후 break). F2가 tasks.json 일관성을 별도로 보장 |
| Webhook payload에 `terminationReason` 추가 시 기존 수신자가 strict schema validator라면 실패 | 낮음 | 외부 시스템 호환 | 선택 사항으로 둠. 기본 false로 둘지 옵션화 검토. 본 PR scope는 게이트 핵심 |

## 6. 검증 시나리오 (구현·테스트 PR에서 사용)

1. **budget 미지정** — `ralph --run`. CheckBudgetAsync 첫 줄 early return.
   비용 파일 read 호출 0건(stat으로 검증 어려우므로 코드 경로 정독으로 입증).
2. **budget=0 또는 음수** — `--budget-usd 0`, `--budget-usd -1`. 미설정과 동일.
3. **budget << 누적비용** — `--budget-usd 0.001`, cost.jsonl에 임의 라인 1개
   삽입. 첫 iteration에서 80% 경고 + 100% 차단. dispatch 0건. 종료 코드 2.
4. **80% 경계 진입 후 중단(차단 미발생)** — 누적이 80% 이상 100% 미만. 경고 1회,
   계속 dispatch. 동일 세션에서 다시 80%를 지나도 경고 추가 안 됨(`_budgetWarningEmitted`).
5. **100% 도중 도달** — 첫 batch는 정상 시작/완료. 둘째 batch 시작 직전 합산이
   임계 도달 → break. 진행 중 task는 강제 cancel 없이 정상 완료된 흔적이 logs에 남음.
6. **CLI vs env 우선순위** — `RALPH_BUDGET_USD=10 ralph --run --budget-usd 1`. 합산 ≥1
   일 때 차단(=CLI 우선).
7. **InvariantCulture 파싱** — `--budget-usd 0.5` 정상, `--budget-usd 0,5` 명시적 에러.
8. **순차 경로** — `--sequential --budget-usd 0.001` → `RunAutoLoop`이 첫 iteration에
   `return 2`. main 종료 코드 2.
9. **종료 코드 main 노출** — `exit code` 검증을 process 호출자(shell `$?`)에서 직접 확인.
10. **Webhook 후방 호환** — `RALPH_WEBHOOK_URL` 설정 + budget 차단 시 onFailure(또는
    onComplete) POST 1건. payload `success=false`. (선택 구현 시 `terminationReason=budget_reached`).
11. **worktree 잔존 검사** — 차단 후 `git worktree list`에 ralph/ 항목 없음.
12. **`tasks.json` 일관성** — 차단 시점까지 완료된 task만 done=true, 나머지
    pending. `ralph --status`로 확인.

## 7. 비목적 (Out of Scope)

- **진행 중 task 강제 cancel.** 요구사항이 명시적으로 금지. 데이터 손상
  방지 우선.
- **CostTracker → decimal 마이그레이션.** 별도 P2(전 경로 기반 변경 필요).
- **비용 추정 정확도 향상.** 단가표(`Pricing`) 갱신은 본 PR scope 외.
- **`--task`/`--interactive`/`--dry-run` 게이트.** 이 명령들은 단발이거나
  사람의 결정 단계라 자동 게이트가 적합하지 않다. P2.
- **메모리 캐시.** `cost.jsonl` 매 read를 단일 in-memory 누적으로 대체하는
  최적화는 P2.
- **다중 budget 단위(EUR, KRW, ...).** USD only.
- **task 단위 budget(요구사항: 누적만).** 본 PR은 세션 누적 합산만 다룬다.
- **soft / hard budget 분리.** 단일 임계값에 80%/100% 두 단계 동작이 끝.
  warn-only 전용 budget은 P2.
- **dashboard에 실시간 budget bar 표시.** Spectre.Console UI 변경은 별도 과제.
- **`--cost`에 budget 진척도 표기.** 별도 짧은 후속 PR로 분리.

## 8. 결론

`CostTracker.GetTotalUsdAsync()`로 cost.jsonl 합산을 단일 출처화하고,
`ParallelExecutor.RunAsync`의 while 루프 시작점과 `RunAutoLoop`의 while 루프
시작점에 동일 의미의 budget 게이트를 둔다. CLI `--budget-usd <amount>`가
환경변수 `RALPH_BUDGET_USD`보다 우선이며, 미지정·0·음수는 모두 미설정과
동일 처리해 기존 동작을 100% 보존한다. 임계값의 80% 도달 시 1회 경고,
100% 도달 시 새 dispatch 차단·진행 중은 끝까지 대기·종료 코드 2를 반환한다.
강제 종료는 하지 않으며, 세션 종료 webhook이 `success=false` 형태로 자연
발화한다(터미네이션 사유 필드는 선택 확장).
