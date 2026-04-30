# fix2 #1 — CostTracker 정적 상태 → 인스턴스화 + DI 설계

## 0. 목적

`Ralph/Services/CostTracker.cs`는 누적 비용 캐시(`_cumulativeUsd`),
hydration 플래그(`_hydrated`), log dir override(`_logDirOverride`),
`Pricing` 사전을 **정적 필드**로 보유한다. 이로 인해:

- 동일 프로세스에서 여러 `CostTracker` 인스턴스를 만들어도 누적값이 공유되어
  격리된 회계가 불가능 (예: 다른 워크스페이스, 다른 logDir).
- 테스트는 `ResetForTesting()` / `SetLogDirForTesting()`을 매번 호출해야 하고
  `[Collection("cost")]`로 직렬화해야만 안전 — 병렬 테스트 시 깨짐.
- DI/테스트 더블 주입이 어려움.

본 task는 이를 인스턴스 기반으로 전환하면서 `CommandContext`에서 단일
인스턴스를 주입하는 설계를 확정한다. **본 문서는 설계 산출물이며 구현
변경은 후속 task(fix2 #1 impl/test)에서 수행한다.**

---

## 1. 현재 정적 상태 인벤토리

### 1.1 `Ralph/Services/CostTracker.cs`의 정적 필드 (라인 번호는 현재 HEAD 기준)

| 필드 / 메서드 | 선언 라인 | 종류 | 역할 | 인스턴스화 후 분류 |
|---|---|---|---|---|
| `_logDirOverride` | 47 | static field | 테스트용 로그 디렉터리 override | **인스턴스 필드**(생성자 인자) |
| `LogDir` | 48 | static prop | `_logDirOverride ?? RalphPaths.LogDir` | **인스턴스 prop** |
| `Pricing` | 51 | static readonly | EmbeddedResource pricing.json (1회 로드) | **타입 단위 readonly 유지** (불변, 부작용 없음) |
| `JsonOpts` | 54 | static readonly | JsonSerializerOptions | **타입 단위 readonly 유지** |
| `HydrateLock` | 62 | static `SemaphoreSlim` | hydration race 방지 | **인스턴스 필드** |
| `IncrementLock` | 63 | static `object` | `_cumulativeUsd` 갱신 mutex | **인스턴스 필드** |
| `WriteLock` | 68 | static `SemaphoreSlim` | jsonl append 직렬화 (P-CONCURRENCY) | **인스턴스 필드** + 파일 단위 lock 보강 (§7.4) |
| `_cumulativeUsd` | 69 | static double | 누적 비용 캐시 | **인스턴스 필드** |
| `_hydrated` | 70 | static bool | hydrate 1회 플래그 | **인스턴스 필드** |
| `WriteTimeout` | 93 | static readonly | 기록 timeout (5s) | **타입 단위 readonly 유지** |
| `ResetForTesting()` | 78 | static method | 테스트 격리 | **제거** (인스턴스 재생성으로 대체) |
| `SetLogDirForTesting()` | 90 | static method | 테스트용 logDir override | **제거** (생성자 인자로 대체) |
| `EstimateUsd(model, u)` | 184 | public static | 단순 계산 (Pricing 의존) | **public static 유지** (순수 함수) |
| `NormalizeModel(model)` | 195 | internal static | 모델 키 정규화 | **internal static 유지** (Pricing은 불변) |
| `NormalizeModel(model, pricing)` | 198 | internal static | 위 오버로드 (테스트용) | **internal static 유지** |
| `LoadPricing()` | 226 | private static | Pricing 초기화 | **private static 유지** (`Pricing` 초기화에 사용) |

### 1.2 호출 지점 (grep `CostTracker`)

**프로덕션 코드**

| 파일 | 라인 | 호출 형태 |
|---|---|---|
| `Ralph/Commands/RunCommand.cs` | 108 | `var costTracker = new CostTracker();` (이미 단일 인스턴스 공유) |
| `Ralph/Commands/InteractiveCommand.cs` | 34 | `new SequentialRunner(..., new CostTracker())` |
| `Ralph/Commands/SingleTaskCommand.cs` | 80 | `new SequentialRunner(..., new CostTracker())` |
| `Ralph/Commands/DryRunCommand.cs` | 41, 44 | `new CostTracker()` 두 번 (Sequential + RunAutoLoop 인자) |
| `Ralph/Commands/PlanCommand.cs` | 241 | `var cost = new CostTracker();` (LLM critique 비용 기록) |
| `Ralph/Commands/CostCommand.cs` | 10 | `var tracker = new CostTracker();` |
| `Ralph/Services/ParallelExecutor.cs` | 50, 62 | 생성자 인자, fallback `new CostTracker()` |
| `Ralph/Services/SequentialRunner.cs` | 23, 124 | 생성자 인자, RunAutoLoopAsync 인자 |
| `Ralph/Services/MergeOrchestrator.cs` | 19, 31 | 생성자 인자 |
| `Ralph/Services/VerificationLoop.cs` | 16, 20 | 생성자 인자 |
| `Ralph/Services/BudgetGate.cs` | 13, 18 | 생성자 인자 |
| `Ralph/Services/TaskProgressTracker.cs` | 31, 40 | `AttachCostTracker(CostTracker, double?)` |

**테스트 코드** (모두 `[Collection("cost")]`로 직렬화 — `Ralph.Tests/CostCollection.cs`)

- `Ralph.Tests/CostTrackerTests.cs` — 정적 setup/teardown으로 `SetLogDirForTesting`·`ResetForTesting` 호출.
- `Ralph.Tests/CostTrackerConcurrencyTests.cs` — 동일 패턴.
- `Ralph.Tests/ParallelExecutorTests.cs` — l. 47–48, 56–57.
- `Ralph.Tests/MergeOrchestratorFailureTests.cs` — l. 41–42, 50–51.
- `Ralph.Tests/BudgetCancelConsistencyTests.cs` — l. 25–26, 31–32.
- `Ralph.Tests/BudgetGateTests.cs` — l. 15–16, 21–22.
- `Ralph.Tests/ClaudeServiceLargePromptTests.cs` — l. 22–23, 28–29.
- `EstimateUsd(...)`, `NormalizeModel(...)` 정적 메서드는 그대로 호출됨 (`CostTrackerTests.cs:55, 72, 79, 96, 103–107`).

### 1.3 정적 상태가 일으키는 실제 문제

1. `RunCommand`는 이미 단일 `CostTracker` 인스턴스를 공유하므로 정적 캐시는
   사실상 불필요한 전역 결합. `DryRunCommand`는 `new CostTracker()`를 두
   번 만들지만 정적 필드 덕에 **우연히** 같은 누적값을 공유.
2. 테스트는 `[Collection("cost")]` 직렬화로 우회 중 — 병렬 테스트 가능성을
   잃음.
3. 향후 멀티-워크스페이스/embedded 사용 (다른 logDir에 동시 기록)이 불가.

---

## 2. 인스턴스 클래스 설계

### 2.1 생성자 시그니처

```csharp
public sealed class CostTracker
{
    private readonly string _logDir;
    private readonly RalphLogger _logger;

    private readonly SemaphoreSlim _hydrateLock = new(1, 1);
    private readonly object _incrementLock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private double _cumulativeUsd;
    private bool _hydrated;

    public CostTracker(string? logDir = null, RalphLogger? logger = null)
    {
        _logDir = logDir ?? RalphPaths.LogDir;
        _logger = logger ?? RalphLogger.Null;
    }

    public string LogFilePath => Path.Combine(_logDir, RalphPaths.CostLedgerFileName);

    // 타입 단위로 유지하는 정적 멤버 (불변):
    private static readonly Dictionary<string, PricingEntry> Pricing = LoadPricing();
    private static readonly JsonSerializerOptions JsonOpts = ...;
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    public static double EstimateUsd(string model, TokenUsage u) { ... }
    internal static string NormalizeModel(string model) { ... }
    internal static string NormalizeModel(string model, IReadOnlyDictionary<string, PricingEntry> pricing) { ... }
}
```

설계 결정:

- **`logDir` 인자**: 기본값 `null` → `RalphPaths.LogDir`. 테스트는 임시 디렉터리
  경로를 그대로 주입 — `SetLogDirForTesting` 정적 사이드이펙트 제거.
- **`logger` 인자**: optional. fix5-silent-errors-plan에서 logger 주입 라인이
  이미 합의되어 있으나(§docs/plans/fix5-silent-errors-plan.md:255–261) 본
  task 범위는 logger 추가 *위치 확보*까지로 한다 — `RalphLogger.Null` fallback을
  허용해 호출지를 깨뜨리지 않는다. 실제 fallback 경로별 `Warn` 호출은 fix5
  task에서 추가.
- **`Pricing`/`JsonOpts`/`WriteTimeout`은 static readonly 유지**: 불변이고 모든
  인스턴스가 동일 단가를 사용하므로 인스턴스화하면 메모리만 낭비. 사용자
  override(`~/.ralph/pricing.json`)도 프로세스 시작 1회 로드로 충분.
- **`EstimateUsd` / `NormalizeModel`은 public/internal static 유지**: 순수
  함수. 기존 테스트가 정적 형태로 호출(`CostTrackerTests.cs:55, 72, 96` 등)하므로
  바꾸면 이득 없이 회귀만 유발.
- **`sealed` 추가**: 상속 의도 없음, 인터페이스 확장은 §6 참조.

### 2.2 인스턴스 메서드 시그니처 변화

| 메서드 | 변경 전 | 변경 후 |
|---|---|---|
| `RecordAsync` | `public async Task` | 그대로 — `LogDir` 참조가 `_logDir`로 |
| `RecordInnerAsync` | `private async Task` | `_writeLock`, `_incrementLock`, `_cumulativeUsd`, `_hydrated`로 갱신 |
| `EnsureHydratedAsync` | `private` | `_hydrateLock`, `_hydrated`, `_cumulativeUsd` |
| `ReadTotalFromDiskAsync` | `private` | 변화 없음 (`LogFilePath` 의존) |
| `GetTotalUsdAsync` | `public` | `_incrementLock`, `_cumulativeUsd` |
| `PrintSummaryAsync` | `public` | `LogFilePath`만 의존 — 무변화 |
| `LogFilePath` (prop) | `Path.Combine(LogDir, ...)` | `Path.Combine(_logDir, ...)` |
| `ResetForTesting()` | static | **제거** |
| `SetLogDirForTesting(path)` | static | **제거** |

---

## 3. CommandContext 주입 설계

### 3.1 현재 패턴 (StateStore 비교)

`CommandContext.cs`는 *입력 + 팩토리* 모음으로, `IAgentRunner`를
`NewClaudeService(tm)`로 만들어 준다. `StateStore`는 `TaskManager.LoadAsync`
내부에서 자동 생성되므로 `CommandContext`에 직접 노출되지 않는다.

`CostTracker`는 **세션-범위 single instance**가 필요하다 (jsonl append + 누적
캐시 일관성). 따라서 `CommandContext`에 *팩토리*가 아닌 **lazy 단일
인스턴스 프로퍼티**로 노출하는 것이 자연스럽다.

### 3.2 추가할 멤버

```csharp
public sealed class CommandContext
{
    // ... 기존 필드들 ...

    private CostTracker? _cost;
    /// <summary>
    /// 세션 단일 CostTracker. 처음 호출 시 RalphPaths.LogDir 기준으로 1회 생성.
    /// 모든 command/runner가 동일 인스턴스를 공유해 누적 캐시·jsonl writer를 일관되게 유지한다.
    /// </summary>
    public CostTracker Cost => _cost ??= new CostTracker(logDir: null, logger: null);
}
```

설계 결정:

- **lazy 생성**: `CostCommand`처럼 `Cost`만 사용하는 명령은 그 시점에만 비용
  발생. 또한 일부 단위 테스트는 `CommandContext`를 직접 만들 수 있고 그
  경우에도 disk IO를 강제하지 않는다 (`CostTracker` 생성자 자체는 IO 없음).
- **lifetime**: `CommandContext`는 `Program.cs`에서 ralph 호출당 1회 생성 →
  `Cost`도 ralph 호출당 1개. 이는 현 `RunCommand.cs:108`의 의도(공유)와 일치.
- **`logger` 주입은 본 task 범위 외**: fix5 task가 `CostTracker(logDir, logger)`
  시그니처에 logger를 채우는 식으로 합쳐진다.
- **테스트 logDir override**: `CommandContext`는 record 형태가 아니므로
  생성자에서 `Cost`를 직접 초기화하지 않고 lazy로 두면, **테스트 코드는
  `_ctx`를 우회하고 자체 `CostTracker(tempDir)`를 만들어 자기 객체에만
  Record/Get 호출**하면 된다 (정적 사이드이펙트가 없으므로).

### 3.3 명령 측 마이그레이션 패턴

원칙: **`new CostTracker()` 호출지를 모두 `_ctx.Cost` 참조로 교체**.

---

## 4. 호출 지점별 마이그레이션 스케치

### 4.1 `Ralph/Commands/RunCommand.cs:107-108`

```csharp
// before
// P1-3: 단일 CostTracker 인스턴스 공유.
var costTracker = new CostTracker();

// after
var costTracker = _ctx.Cost;
```

이후 `costTracker`를 `ParallelExecutor` / `SequentialRunner`에 그대로 전달
(주변 코드 무변경).

### 4.2 `Ralph/Commands/InteractiveCommand.cs:34`

```csharp
// before
var runner = new SequentialRunner(tm, claude, git, logger, _ctx.TasksFile, modelOverride, new CostTracker());

// after
var runner = new SequentialRunner(tm, claude, git, logger, _ctx.TasksFile, modelOverride, _ctx.Cost);
```

### 4.3 `Ralph/Commands/SingleTaskCommand.cs:80`

```csharp
// before
var runner = new SequentialRunner(tm, claude, git, logger, _ctx.TasksFile, modelOverride, new CostTracker());

// after
var runner = new SequentialRunner(tm, claude, git, logger, _ctx.TasksFile, modelOverride, _ctx.Cost);
```

### 4.4 `Ralph/Commands/DryRunCommand.cs:41, 44`

```csharp
// before
var runner = new SequentialRunner(tm, claude, git, logger, _ctx.TasksFile, modelOverride, new CostTracker());
result = await runner.RunAutoLoopAsync(
    dryRun: true, commitOnComplete: false, budgetUsd: null,
    cost: new CostTracker(), ct);

// after
var runner = new SequentialRunner(tm, claude, git, logger, _ctx.TasksFile, modelOverride, _ctx.Cost);
result = await runner.RunAutoLoopAsync(
    dryRun: true, commitOnComplete: false, budgetUsd: null,
    cost: _ctx.Cost, ct);
```

(현재 두 인스턴스가 우연히 정적 캐시로 통합되던 부분이 명시적 단일
인스턴스로 정리됨)

### 4.5 `Ralph/Commands/PlanCommand.cs:241`

```csharp
// before
var cost = new CostTracker();

// after
var cost = _ctx.Cost;
```

### 4.6 `Ralph/Commands/CostCommand.cs:10`

```csharp
// before
var tracker = new CostTracker();
await tracker.PrintSummaryAsync(ct);

// after
await _ctx.Cost.PrintSummaryAsync(ct);
```

(`CostCommand`에 `_ctx` 주입이 안 되어 있으면 해당 명령에 `CommandContext`
의존성을 추가 — 다른 command와 동일 패턴.)

### 4.7 `Ralph/Services/ParallelExecutor.cs:50, 62`

```csharp
// before
public ParallelExecutor(
    ...,
    CostTracker? cost = null, BudgetGate? budgetGate = null,
    ...)
{
    ...
    _cost = cost ?? new CostTracker();
    ...
}

// after
public ParallelExecutor(
    ...,
    CostTracker cost, BudgetGate? budgetGate = null,
    ...)
{
    ...
    _cost = cost;
    ...
}
```

`cost`를 nullable에서 required로 승격 — 호출자는 항상 `_ctx.Cost`를 넘긴다.
이는 정적 fallback에 의존하던 잠재 결함을 컴파일 타임에 차단.

### 4.8 `Ralph/Services/SequentialRunner.cs:23, 124`

생성자/`RunAutoLoopAsync` 모두 이미 `CostTracker` 인자를 요구. 변경 없음.

### 4.9 `Ralph/Services/MergeOrchestrator.cs`, `VerificationLoop.cs`, `BudgetGate.cs`, `TaskProgressTracker.cs`

모두 이미 `CostTracker` 인자를 받음. 시그니처 무변경.

---

## 5. Static facade — 유지 vs 제거

**결정: facade 미생성, 정적 진입점 전면 제거.**

근거:

- 외부 호출자는 모두 ralph 자체 코드 (위 §1.2). **Ralph의 타 프로젝트나
  서드파티가 `CostTracker.ResetForTesting`/`SetLogDirForTesting`을 호출하는
  지점은 없음**. 라이브러리로 노출되어 있지도 않음 (binary CLI).
- `EstimateUsd`/`NormalizeModel`은 *정적 순수 함수* — facade가 아니라 **그
  자체로 인스턴스 무관**. 이미 그대로 유지하므로 facade 개념이 필요 없다.
- `ResetForTesting`/`SetLogDirForTesting`은 *목적 자체가 정적 상태 우회*.
  인스턴스화 후 의미를 잃는다. 얇은 facade로 남겨도 동작이 모호해질 뿐.
- fix2.md §1 "기존 정적 진입점이 외부에서 호출되는 곳이 있다면 ... 없으면
  제거"의 *없으면* 분기에 해당.

부산 효과: API 표면이 줄어 향후 변경에 자유로움.

---

## 6. 테스트 격리 방식 변경안

### 6.1 핵심 원리

각 테스트가 **자기 임시 디렉터리를 가진 자기 `CostTracker` 인스턴스**를
만들고 그 객체에만 접근한다. 정적 상태가 사라지므로:

- `[Collection("cost")]` 제거 가능 → 테스트 병렬화 회복.
- `IDisposable` setup/teardown에서 `ResetForTesting`/`SetLogDirForTesting`
  호출 제거.

### 6.2 `Ralph.Tests/CostTrackerTests.cs` 패턴 (변경 후 스케치)

```csharp
// [Collection("cost")] 제거
public class CostTrackerTests : IDisposable
{
    private readonly string _tempDir;

    public CostTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ralph-cost-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task RecordAsync_with_null_result_writes_placeholder_and_does_not_increment()
    {
        var cost = new CostTracker(logDir: _tempDir);
        await cost.RecordAsync("missing-task", "opus", result: null);
        ...
    }
}
```

각 `[Fact]`가 자기 tempDir + 자기 `CostTracker`를 갖는다. 클래스 단위 tempDir도
가능하지만 **테스트 단위 인스턴스 생성을 권장** — 누적 검증의 사이드이펙트
누출을 막는다 (`PrintSummary_omits_conflict_section_when_no_conflict_entries`
같은 검사가 다른 테스트의 잔존 jsonl을 읽지 않도록).

### 6.3 인스턴스 격리 검증 테스트 (신규, fix2.md 요구사항 §1.4)

```csharp
[Fact]
public async Task Two_instances_with_different_logdirs_do_not_share_cumulative()
{
    using var dirA = new TempDir();
    using var dirB = new TempDir();
    var costA = new CostTracker(logDir: dirA.Path);
    var costB = new CostTracker(logDir: dirB.Path);

    var usage = new TokenUsage(1_000_000, 0, 0, 0); // opus: $15
    await costA.RecordAsync("a", "opus",
        new ClaudeResult { Success = true, Usage = usage });

    Assert.Equal(15.0, await costA.GetTotalUsdAsync(), 4);
    Assert.Equal(0.0,  await costB.GetTotalUsdAsync(), 4);
}
```

### 6.4 다른 테스트 클래스 마이그레이션

| 파일 | 변경 |
|---|---|
| `CostTrackerConcurrencyTests.cs` | `[Collection("cost")]` 제거; `new CostTracker()` → `new CostTracker(_tempDir)`. l. 60 `new CostTracker().LogFilePath` → 같은 `_tempDir` 인스턴스의 `LogFilePath` 또는 `Path.Combine(_tempDir, RalphPaths.CostLedgerFileName)` |
| `BudgetGateTests.cs` | `SetLogDirForTesting/ResetForTesting` 제거; 각 테스트가 `new CostTracker(_tempDir)` 사용 |
| `BudgetCancelConsistencyTests.cs` | 동일 |
| `ParallelExecutorTests.cs` | `CostTracker.SetLogDirForTesting/ResetForTesting` 제거; `_repoDir` 기준 `new CostTracker(Path.Combine(_repoDir, RalphPaths.LogDir))`를 `ParallelExecutor`에 직접 주입 |
| `MergeOrchestratorFailureTests.cs` | 동일 (자기 `_logDir` 기준 인스턴스) |
| `ClaudeServiceLargePromptTests.cs` | 동일 |
| `Ralph.Tests/CostCollection.cs` | **파일 삭제** (collection definition 더 이상 필요 없음) |

### 6.5 누적값 정합성 — fix2.md §1.5 ("hydration 정합성 확인")

`EnsureHydratedAsync`는 jsonl 전체를 한 번 읽어 `_cumulativeUsd`를 채운다.
인스턴스 단위로 분리해도:

- 동일 `logDir`을 가리키는 두 인스턴스가 있어도, 각각이 처음 hydrate 시
  같은 disk 파일을 읽어 같은 누적값에서 출발 → 정합성 유지.
- 단, **두 인스턴스가 동시에 같은 파일에 append** 하면 OS-레벨 lock이 없는
  한 인터리브 가능. 본 task의 단일 `_ctx.Cost` 패턴에서는 인스턴스가 1개라
  발생하지 않음. 하지만 향후 다중 인스턴스 시나리오를 위해 §7.4 잔여
  위험으로 추적.

---

## 7. 회귀 위험 및 검증 항목

### 7.1 회귀 위험 매트릭스

| 위험 | 영향 | 완화 |
|---|---|---|
| `_ctx.Cost` 미주입으로 ParallelExecutor가 fallback `new CostTracker()`를 만들어 누적 분리 | 중 | ParallelExecutor의 `cost` 인자를 nullable에서 required로 승격 (§4.7) |
| 테스트가 `[Collection("cost")]`를 제거한 후 다른 테스트가 같은 `~/.ralph-logs/cost.jsonl`을 건드리는 잔존 코드 | 중 | 테스트 setup에서 절대 `RalphPaths.LogDir`(=cwd 기반)을 그대로 사용하지 않도록, 모든 테스트가 `_tempDir`를 명시 주입 — grep으로 `new CostTracker()` (인자 없는 호출)이 테스트에 남아있지 않은지 검증 |
| `CostCommand`의 `_ctx.Cost`가 lazy 생성이라 이전 세션의 jsonl을 hydrate한 누적값을 반환 — 이는 의도된 동작이지만 *현재* 세션 비용만 보고 싶은 사용자는 혼란 가능 | 저 | 현 동작과 동일 (정적 캐시일 때도 jsonl 전체 hydrate). 별도 변경 없음 |
| 정적 facade를 제거함으로써 외부 코드(혹시 있다면)가 깨짐 | 매우 저 | §1.2/§5에서 외부 호출자 부재 확인 완료 |
| `Pricing`이 여전히 정적 readonly이므로 사용자가 런타임에 `~/.ralph/pricing.json`을 바꿔도 미반영 | 저 | 현 동작과 동일 (의도된 1회 로드) |
| `_writeLock`이 인스턴스 단위가 되어 **같은 파일을 가리키는 별개 인스턴스 동시 append 시 sharing violation/라인 손상** | 중 (시나리오 한정) | §7.4 잔여 위험 — 본 task 범위는 단일 `_ctx.Cost` 보장으로 차단; 잔여 위험으로 README/CLAUDE.md에 명시 |
| `BudgetGate`가 새 `CostTracker`로 만들어진 `_ctx.Cost`를 참조하지 못해 budget 임계 검사 불일치 | 중 | `RunCommand`에서 `BudgetGate(budgetUsd, _ctx.Cost, logger)` 단일 인스턴스를 ParallelExecutor/SequentialRunner에 동시 주입 (현 패턴 유지) |

### 7.2 단위 테스트 회귀 항목

- `CostTrackerTests` 전체 PASS (인스턴스 기반).
- `CostTrackerConcurrencyTests.Parallel_record_does_not_corrupt_jsonl` PASS —
  단일 인스턴스 100×5 writers에서 라인 무손실 확인.
- 신규 `Two_instances_with_different_logdirs_do_not_share_cumulative` PASS.
- 신규 `[Collection("cost")]` 제거 후 모든 비용 관련 테스트가 **병렬
  실행에서도** 통과해야 함 — `dotnet test` 기본 설정에서 검증.

### 7.3 통합 검증

- `ralph --run` 후 `ralph --cost` 출력이 정적 버전과 동일한 누적값을 보고
  하는지 (jsonl 디스크 데이터가 ground truth이므로 같아야 함).
- `ralph --plan PRD.md --llm-critique` 시 critique 호출 비용이 jsonl에 추가
  되고 동일 세션의 후속 `ralph --cost`에 반영되는지 — `_ctx.Cost` 단일
  인스턴스 공유로 자연 충족.
- `ralph --run --budget-usd 5.00` budget gate 트리거가 정적 버전과 동일
  지점에서 발화하는지 (`BudgetGateTests` + 통합 시나리오).

### 7.4 잔여 위험 — 후속 작업으로 추적

- **다중 `CostTracker` 인스턴스가 동일 `logDir`에 동시 append**: 본 task 범위
  외. 현재 코드 경로는 이 시나리오를 만들지 않으나, 인스턴스화 자체가
  허용 가능성을 노출. `_writeLock`은 인스턴스 단위 mutex라 다른 프로세스/
  인스턴스를 직렬화하지 못함. fix2 외 별 task에서 OS-레벨 file lock(예:
  `FileShare.None` retry loop) 도입 검토 권장.
- **logger 주입 위치**: 본 설계는 `RalphLogger? logger = null` 자리만 만들고
  fallback 경로에 `Warn` 호출은 추가하지 않는다. fix5-silent-errors-impl이
  해당 호출을 채운다 (§docs/plans/fix5-silent-errors-plan.md:255–334와 정합).

---

## 8. 작업 분해 (후속 impl/test task 입력용)

후속 impl task가 수행해야 할 변경 (참고용 — 본 task 산출물 아님):

1. `Ralph/Services/CostTracker.cs`
   - 정적 → 인스턴스 필드 전환 (§2.1).
   - `ResetForTesting`/`SetLogDirForTesting`/`LogDir` 정적 멤버 제거.
   - `class` → `sealed class`.
   - `Pricing`/`JsonOpts`/`WriteTimeout`/`EstimateUsd`/`NormalizeModel`/
     `LoadPricing`은 static 유지.
2. `Ralph/Commands/CommandContext.cs`
   - `private CostTracker? _cost;` + `public CostTracker Cost => _cost ??= new CostTracker();` 추가.
3. 호출지 6곳 교체 (§4.1–§4.6).
4. `ParallelExecutor.cs` 생성자 `cost`를 required로 승격 (§4.7).
5. 테스트 마이그레이션 (§6.4) + 신규 격리 테스트 (§6.3).
6. `Ralph.Tests/CostCollection.cs` 삭제, `[Collection("cost")]` 어트리뷰트
   전 파일 제거.
7. `dotnet test` 전체 PASS, `dotnet build` 무경고.

---

## 9. 산출물 / 보고

- 생성 파일: `docs/fix2/01-cost-tracker-di-plan.md` (본 문서).
- Scope 외 파일 변경: 없음.
- 추가 컨텍스트 참조: `fix2.md` §1, `docs/plans/fix5-silent-errors-plan.md`
  §`CostTracker는 현재 logger를 받지 않는다`.
