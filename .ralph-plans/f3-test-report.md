# F3 테스트 리포트 — 순차 실행 경로의 비용 기록 통일

- 일시: 2026-04-28
- 대상 구현: `Ralph/Program.cs` (커밋 `fd24b44`, `f3-cost-sequential-impl` 결과물)
- 검증 방법: `dotnet build` + 코드 리뷰 (실제 Claude Code 호출 비용 회피)
- 비교 기준: `Ralph/Services/ParallelExecutor.cs` 의 기존 `RecordAsync` 호출

---

## 결과 요약

| # | 항목 | 결과 |
|---|---|---|
| 1 | `dotnet build Ralph/Ralph.csproj` 통과 | **PASS** |
| 2 | 3개 진입 함수에서 `RunWithRetryAsync` 직후 `CostTracker.RecordAsync` 호출 | **PASS** |
| 3 | 인자/형식이 `ParallelExecutor`와 동일 | **PASS** |
| 4 | model이 null이면 "opus" 기본값 적용 | **PASS** |
| 5 | cost.jsonl 출력 형식 회귀 없음 | **PASS** |
| 6 | 본 리포트 작성 (PASS/FAIL 기록) | **PASS** |

전체 항목 PASS — 추가 코드 수정 없음.

---

## 1. `dotnet build Ralph/Ralph.csproj` 통과 — **PASS**

```
Ralph -> /Users/felix/src/_tool/ralph/Ralph/bin/Debug/net8.0/osx-arm64/ralph.dll
빌드했습니다.
    경고 0개
    오류 0개
경과 시간: 00:00:00.53
```

빌드 산출물 정상 생성. 경고 0, 오류 0.

---

## 2. 3개 진입 함수의 호출 경로 검증 — **PASS**

태스크 지시는 "`RunAutoLoop`, `HandleSingleTask`, `HandleInteractive` 3개 함수에서 `RunWithRetryAsync` 직후 `CostTracker.RecordAsync` 호출"이지만, 구현은 공통 헬퍼로 위임되어 있다. 따라서 각 진입점 → 실제 Claude 호출이 일어나는 위임 함수로의 경로를 추적한다.

| 진입 함수 | 실제 RunWithRetryAsync 호출 위치 | RecordAsync 호출 위치 |
|---|---|---|
| `RunAutoLoop` (`Program.cs:980`) | `RunTaskAuto` 위임 (`Program.cs:1016`) → `Program.cs:929` | `Program.cs:930` |
| `HandleSingleTask` (`Program.cs:364`) | `RunTaskAuto` 위임 (`Program.cs:424`) → `Program.cs:929` | `Program.cs:930` |
| `HandleInteractive` (`Program.cs:429`) | `RunInteractiveLoop` 위임 (`Program.cs:438`) → `Program.cs:1099` | `Program.cs:1100` |

검증한 코드 스니펫.

`Ralph/Program.cs:929-930` (RunTaskAuto, 자동 모드 + 단일 태스크 공통 경로):

```csharp
var result = await claude.RunWithRetryAsync(fullPrompt, model: model, logger: logger, ct: ct);
await new CostTracker().RecordAsync(taskId, model ?? "opus", result, ct);
```

`Ralph/Program.cs:1099-1100` (RunInteractiveLoop, 대화형 모드):

```csharp
var result = await claude.RunWithRetryAsync(fullPrompt, model: model, logger: logger, ct: ct);
await new CostTracker().RecordAsync(nextId, model ?? "opus", result, ct);
```

**중간 코드 없음** — `RunWithRetryAsync` 반환 직후 곧바로 `RecordAsync` 호출. 실패 분기(`if (!result.Success)`)는 `RecordAsync` 이후에 위치하여, **성공/실패 무관하게** 비용이 기록된다(=실패한 호출도 토큰을 소비하므로 기록되어야 함).

추가로 `RunAutoLoop` 자체는 `RunTaskAuto`에 위임만 하며 자체 Claude 호출 없음을 확인 (`Program.cs:980-1029`). `HandleSingleTask` 역시 본문에서 Claude 호출 없이 `RunTaskAuto(..., force: true)` 호출만 수행 (`Program.cs:424-426`).

---

## 3. `ParallelExecutor`의 기존 호출과 인자/형식 동일성 — **PASS**

기준선(`Ralph/Services/ParallelExecutor.cs:143`):

```csharp
await _cost.RecordAsync(taskId, _model ?? "opus", result, ct);
```

비교 대상.

| 위치 | 호출 형태 |
|---|---|
| ParallelExecutor.cs:143 | `await _cost.RecordAsync(taskId, _model ?? "opus", result, ct);` |
| ParallelExecutor.cs:354 | `await _cost.RecordAsync(taskId, _model ?? "opus", result, ct);` |
| Program.cs:930 | `await new CostTracker().RecordAsync(taskId, model ?? "opus", result, ct);` |
| Program.cs:1100 | `await new CostTracker().RecordAsync(nextId, model ?? "opus", result, ct);` |

인자 검증 (`CostTracker.RecordAsync(string taskId, string model, ClaudeResult result, CancellationToken ct)`):

1. **taskId** — 모두 `string`, 현재 실행 중인 태스크의 id (Program.cs:1100은 변수명이 `nextId`이지만 동일 의미. `RunTaskAuto`의 `taskId`와 동등).
2. **model** — `model ?? "opus"` 형태 일치. (ParallelExecutor는 인스턴스 필드 `_model`, Program.cs는 메서드 매개변수 `model`이라 식별자만 다름).
3. **result** — `RunWithRetryAsync` 반환값을 그대로 전달.
4. **ct** — 동일한 `CancellationToken`을 전달.

**유일한 차이**: ParallelExecutor는 인스턴스 필드 `_cost`(`new CostTracker()` 생성자에서 1회 초기화)를 재사용하고, 순차 경로는 호출마다 `new CostTracker()`를 만든다. `CostTracker`에는 관측 가능한 인스턴스 상태가 없고(쓰기는 매번 `Directory.CreateDirectory` + `File.AppendAllTextAsync`, 읽기는 정적 `Pricing` 사전 + `JsonOpts`), 따라서 동작 동등성에는 영향 없음. 회귀 위험 없음.

---

## 4. model이 null인 경우 "opus" 기본값 — **PASS**

- 호출 측: `model ?? "opus"` 로 null-coalescing → null이면 `"opus"` 문자열 전달. 두 호출(Program.cs:930, 1100) 모두 동일 패턴.
- 수신 측: `CostTracker.RecordAsync`는 `model` 매개변수를 그대로 `entry.Model`에 기록(`CostTracker.cs:56`)하고, `EstimateUsd` 계산 시 `NormalizeModel`을 통과시킨다(`CostTracker.cs:62, 70-89`).
- 추가 안전망: 만약 누군가 빈 문자열을 넘겨도 `NormalizeModel`은 `string.IsNullOrEmpty` 분기에서 `"opus"`로 정규화 (`CostTracker.cs:83`).

→ Program 레벨의 null → "opus" 변환과 CostTracker 레벨의 fallback이 이중 보호. 정상.

---

## 5. cost.jsonl 출력 형식 회귀 없음 — **PASS**

`F3-impl`은 호출 측만 추가했고 `CostTracker.RecordAsync` / `CostEntry` 자체에는 손대지 않았다(`git log -p Ralph/Services/CostTracker.cs` 기준 변경 없음). 따라서 직렬화 결과 필드/순서는 그대로다.

`CostEntry` 필드 정의(`Ralph/Services/CostTracker.cs:6-17`)와 `JsonOpts`(`CamelCase`, `WriteIndented = false`)에 따른 한 라인 출력 스키마.

```
{"taskId":"…","model":"opus|sonnet|haiku|<other>","timestampUtc":"…Z","inputTokens":…,"outputTokens":…,"cacheReadTokens":…,"cacheCreationTokens":…,"estimatedUsd":…,"durationSec":…}
```

필드 9개·camelCase·라인당 1엔트리·`\n` 종결. 순차 경로가 추가되어도 `RecordAsync` 1회 호출당 정확히 한 줄이 추가되며, ParallelExecutor 경로의 출력과 바이트 수준에서 동일한 스키마를 사용한다.

부수 동작도 회귀 없음:

- `result.Usage == null` 인 경우 조용히 return (`CostTracker.cs:48`) → 캐시 미스/실패 응답에서 빈 라인을 만들지 않음.
- 누적 모드(`File.AppendAllTextAsync`)이므로 기존 라인 보존.

---

## 6. 항목별 PASS/FAIL 기록 — **PASS**

본 문서가 그것이며, 위 5개 항목 전부 PASS. F3 구현은 수용 기준을 만족한다.

---

## 결론

- 코드 변경 필요 없음 (`Ralph/Program.cs`는 그대로 둔다).
- F3-impl(`fd24b44`)이 추가한 두 개의 `await new CostTracker().RecordAsync(...)` 호출이 자동/단일/대화형 3가지 순차 경로 전부를 커버하며, 병렬 경로(`ParallelExecutor`)와 인자·형식·결과 스키마가 일치한다.
- 부수 효과: 이제 `--task <id>`(단일 실행)와 `--interactive`(대화형) 모드에서도 `cost.jsonl`이 누적되어 `ralph --cost`(또는 `CostTracker.PrintSummaryAsync`) 요약에 포함된다.

---

## 작업 보고

- **수정/생성 파일**: `.ralph-plans/f3-test-report.md` (신규 생성)
- **Scope 외 변경**: 없음
- **빌드 결과**: 경고 0, 오류 0
- **결함 발견**: 없음 → `Ralph/Program.cs` 수정 미실시
