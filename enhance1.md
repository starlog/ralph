# Ralph Enhancement 1 — 8개 deeper-concern 일괄 보완

본 문서는 `ralph --plan enhance1.md`로 tasks.json을 생성한 뒤
`ralph --run`으로 자가 적용하기 위한 PRD입니다. 각 feature는 **독립 가능한 단위**로
설계되어 worktree 병렬 실행이 가능합니다. 단, 같은 파일을 건드리는 feature는
`dependsOn`으로 직렬화합니다.

## 프로젝트 개요

- 프로젝트: `ralph` (.NET 8 CLI 태스크 오케스트레이터)
- 주 코드 위치: `Ralph/`, 테스트: `Ralph.Tests/`
- 언어/스택: C# 12 / .NET 8, Spectre.Console
- 빌드: `dotnet build Ralph/Ralph.csproj -nologo`
- 테스트: `dotnet test`
- 커밋 메시지: 한국어로 작성 (CLAUDE.md 규칙)

## 공통 작업 규칙

- 모든 task의 `verification.command`는 `dotnet build Ralph/Ralph.csproj -nologo`
  또는 관련 `dotnet test` 명령으로 지정.
- `tasks.json`은 절대 수정 금지 (worktree 격리, ralph가 자동 관리).
- 각 feature는 1~2 task로 구성 (granularity: small). 4-phase로 부풀리지 말 것.
- 수정 파일을 `modifiedFiles`에 정확히 명시 — ralph의 병렬 충돌 감지에 사용됨.
- 새 CLI flag는 `Program.cs`의 `--help` 출력과 `README.md`/`README.ko.md`의
  옵션 표에도 한 줄 추가할 것.

---

## Feature 1 — Pricing의 generation-specific key 매칭

**문제 파일:** `Ralph/Services/CostTracker.cs:193-201`, `pricing.json:3-7`

`NormalizeModel`이 `lower.Contains("opus") → "opus"` 식 family fold 한 단계뿐.
이는 "Opus 4와 Opus 4.7이 같은 단가"라는 가정 위에 성립. Anthropic이
generation별로 단가를 갈라내면 즉시 깨진다.

**수정:**
1. `pricing.json`에 generation-specific key를 추가 (현 단가는 family와 동일하게
   복제해 회귀 없음). 예:
   ```json
   {
     "models": {
       "opus":     { ... },
       "opus-4":   { ... },
       "opus-4-7": { ... },
       "sonnet":   { ... },
       "sonnet-4": { ... },
       "sonnet-4-6": { ... },
       "haiku":    { ... },
       "haiku-4-5": { ... }
     }
   }
   ```
2. `NormalizeModel`을 longest-prefix 매칭으로 교체:
   - 입력 `claude-opus-4-7-20251101` 같은 raw model ID에 대해
     `opus-4-7` → `opus-4` → `opus` 순으로 가장 specific한 key를 우선 반환.
   - 매칭 알고리즘: pricing 사전의 모든 key 중 `lower`에 포함되는 것들을
     모은 뒤 길이 desc 정렬해 첫 번째 반환. 매치 없으면 lower 그대로.
3. 단위 테스트 추가:
   - `claude-opus-4-7` → `opus-4-7` 우선
   - `claude-opus-4` → `opus-4`
   - `claude-opus` → `opus`
   - 빈 문자열 → `opus` (기본 fallback 유지)

**verification:** `dotnet test --filter "FullyQualifiedName~CostTracker" -nologo`

**modifiedFiles:**
- `Ralph/Services/CostTracker.cs`
- `pricing.json`
- `Ralph.Tests/CostTrackerTests.cs`

---

## Feature 2 — IAgentRunner 추상화 검증용 MockAgentRunner

**문제 파일:** `Ralph/Services/IAgentRunner.cs`, 단일 구현체(`ClaudeService`)만 존재

추상화는 있으나 구현체가 하나라서 인터페이스 적합성이 검증되지 않음. Aider/Codex
실제 구현은 큰 작업이므로 본 PRD 범위 밖. 대신 **테스트용 MockAgentRunner**를
넣어 (a) 인터페이스가 외부 구현체에 충분한지, (b) Feature 8의 ParallelExecutor
integration test에 의존성 주입이 가능한지를 동시에 검증한다.

**수정:**
1. `Ralph.Tests/Helpers/MockAgentRunner.cs` 신규 작성:
   - `IAgentRunner`를 구현.
   - 생성자에서 `Func<string, ClaudeResult>` (prompt → result) 받아 호출 시 그대로 반환.
   - `RunWithRetryAsync`는 `RunStreamAsync`를 1회 호출.
   - 호출 횟수와 마지막 prompt를 expose해 테스트가 assert 가능하게.
2. 단위 테스트 1개 (`MockAgentRunnerTests.cs`):
   - 주입한 결과가 그대로 반환되는지.
   - `RunWithRetryAsync`가 호출 횟수를 1 증가시키는지.

**verification:** `dotnet test --filter "FullyQualifiedName~MockAgentRunner" -nologo`

**modifiedFiles:**
- `Ralph.Tests/Helpers/MockAgentRunner.cs`
- `Ralph.Tests/MockAgentRunnerTests.cs`

---

## Feature 3 — `--llm-critique` 옵션으로 LLM-based PRD 비평 추가

**문제 파일:** `Ralph/Services/PrdCritic.cs` (현재 100% 정적 분석)

정적 분석은 syntax-level만 본다. "이 PRD는 microservice를 monolith처럼 적었다",
"phase 2에 cross-feature dep이 너무 많다" 같은 의미 수준 critique은 LLM이 필요.
`--plan` 직후 옵션 단계로 한 번 더 LLM critic을 돌려 권고를 출력한다.

**수정:**
1. `Ralph/Services/LlmCritic.cs` 신규:
   - `Task<string> AnalyzeAsync(string prdContent, TaskManager tm, IAgentRunner runner, string? model, CancellationToken ct)`
   - PRD 본문 + 생성된 tasks.json 요약(id/dependsOn/modifiedFiles만)을 prompt로
     구성해 `IAgentRunner.RunStreamAsync`(tools 비활성)로 호출.
   - prompt는 "구조적 문제, 병렬화 누락, scope 과대/과소, 의존성 cycle 위험"을
     bullet 형태로 5개 이내 권고하라는 지시.
   - 반환은 plain text. 호출자가 콘솔에 그대로 출력.
2. `Ralph/Program.cs`에 `--llm-critique` flag 추가:
   - `--plan` 또는 `--critique`와 단독 사용 가능.
   - 기본은 off. 사용자가 명시할 때만 호출 (cost 발생).
   - 호출 결과는 `_cost.RecordAsync($"critique:{tasksFileBase}", ...)`로 별도 line.
3. `--help` 표 갱신, README/README.ko.md의 옵션 섹션에 한 줄 추가.
4. 단위 테스트는 `MockAgentRunner`(Feature 2 산출)를 주입해 prompt가 PRD 본문을
   포함하는지 + cost 기록이 `critique:` 접두사인지 검증.

**dependsOn:** Feature 2 (MockAgentRunner 사용).

**verification:** `dotnet test --filter "FullyQualifiedName~LlmCritic" -nologo`

**modifiedFiles:**
- `Ralph/Services/LlmCritic.cs`
- `Ralph/Program.cs`
- `Ralph.Tests/LlmCriticTests.cs`
- `README.md`
- `README.ko.md`

---

## Feature 4 — Worktree에 `--shared` 옵트인 옵션

**문제 파일:** `Ralph/Services/WorktreeService.cs:86-87`

`git worktree add`가 full checkout을 만들어 N×worktree에서 working tree 전체가
복제됨. 큰 monorepo면 GB 단위. enterprise 사용자에게 결정적 차별점이 될 수 있음.

**수정:**
1. `WorktreeService.CreateWorktreeAsync` 시그니처에 `bool sharedObjects = false`
   추가. true면 `git worktree add --shared`(또는 `--no-checkout` 후 sparse 적용)
   를 시도. git 버전이 미지원하면 graceful fallback + logger.Warn.
   - 호환성 노트: `--shared`는 git 2.10+. `git --version` 파싱은 PRD 범위 밖,
     단순히 명령 실패 시 기존 경로로 fallback.
2. `Ralph/Models/TasksFile.cs`의 `ParallelConfig`에 `bool? SharedWorktreeObjects { get; set; }`
   추가 (default null = false).
3. `tasks.json` 스키마(`ralph-schema.json`)에 `parallel.sharedWorktreeObjects` 키 추가.
4. `Program.cs`에 CLI `--shared-worktrees` flag 추가 (env: `RALPH_SHARED_WORKTREES`).
   우선순위: CLI > env > tasks.json > false.
5. `ParallelExecutor`가 `CreateWorktreeAsync`를 호출할 때 이 값을 전달.
6. README/README.ko.md의 "Parallel Execution Flow" 또는 "Environment Variables"
   섹션에 한 줄 추가.

**verification:** `dotnet build Ralph/Ralph.csproj -nologo`

**modifiedFiles:**
- `Ralph/Services/WorktreeService.cs`
- `Ralph/Services/ParallelExecutor.cs`
- `Ralph/Models/TasksFile.cs`
- `Ralph/Program.cs`
- `ralph-schema.json`
- `README.md`
- `README.ko.md`

---

## Feature 5 — `--cost` 요약에서 conflict 비용 별도 row

**문제 파일:** `Ralph/Services/CostTracker.cs:296-389` (`PrintSummaryAsync`)

`ParallelExecutor.cs:892`에서 conflict resolution은 이미 `conflict:{taskId}`
prefix로 별도 line attribution됨. 하지만 `PrintSummaryAsync`의 "태스크별 상위 10개"
표에서는 일반 task와 같은 행으로 섞여 사용자가 충돌 비용을 단번에 인지하기 어렵다.

**수정:**
1. `PrintSummaryAsync`에 새 섹션 "충돌 해결 비용" 추가:
   - `entries`를 `TaskId.StartsWith("conflict:")` 기준으로 분리.
   - conflict 합계: 호출 수, input/output 토큰, USD 합계, 평균 USD.
   - 0건이면 섹션 자체를 출력하지 않음.
2. 기존 "태스크별 상위 10개"는 conflict line을 제외하고 집계 (사용자가 이미
   별도 섹션으로 보기 때문).
3. `--cost`의 출력 순서: 전체 합계 → 충돌 비용(있을 때) → 태스크별 상위 10개.
4. 단위 테스트:
   - cost.jsonl에 `conflict:foo` line 2개 + `foo` line 1개를 넣고
     `PrintSummaryAsync` 결과에서 충돌 합계가 분리되는지 (`StringWriter` 캡처).

**verification:** `dotnet test --filter "FullyQualifiedName~CostTracker" -nologo`

**modifiedFiles:**
- `Ralph/Services/CostTracker.cs`
- `Ralph.Tests/CostTrackerTests.cs`

**dependsOn:** Feature 1 (`CostTracker.cs` 직렬화).

---

## Feature 6 — README에 self-fix case study chapter 추가

**문제 파일:** `README.md`, `README.ko.md`

현재 README는 기능 나열 위주. `bugfix.md`로 수행한 ralph 자가 수정 경험이
"AI orchestrator가 자기 자신을 고친다"는 강한 traction 스토리지만 README에서
보이지 않음.

**수정:**
1. README.md에 "## Case Study — Ralph Fixes Itself" 섹션을 "How It Works" 직후에 추가:
   - bugfix.md PRD에서 출발해 N개 버그를 병렬 worktree로 수정 → 머지 → 통과한
     과정을 4~6 bullet로 요약.
   - 핵심 수치 (병렬 task 수, wall-clock, 비용 등 — `bugfix.md`에 기록된 것 중 가능한 것).
   - "전체 PRD: [bugfix.md](bugfix.md)" 링크.
2. README.ko.md에 동일 내용을 한국어로 추가.
3. 첫 단락(line 7)의 "first parallel-execution Ralph implementation" 문장은
   유지 (이미 강한 hook이라 변경 불필요).

**verification:** `markdown-link-check` 같은 도구는 환경 의존성이 크므로
대신 `test -f bugfix.md && grep -q "Case Study" README.md && grep -q "사례 연구\|Case Study" README.ko.md`
를 verification.command로 사용.

**modifiedFiles:**
- `README.md`
- `README.ko.md`

---

## Feature 7 — Smoke test를 opt-out 모델로 전환

**문제 파일:** `Ralph/Services/ParallelExecutor.cs:954-988` (`RunPostMergeSmokeTestAsync`)

현재 `workflow.smokeTest`가 명시 지정될 때만 실행. 미지정이면 머지 후
"build broken"을 잡을 방법이 없다. opt-in이라 사용자가 모르고 지나치는 리스크.

**수정:**
1. `ParallelExecutor.RunPostMergeSmokeTestAsync`가 spec이 null일 때:
   - 자동 추론 시도 (순서):
     - `dotnet sln list` 또는 `*.csproj` 발견 → `dotnet build -nologo` 사용
     - `package.json` 발견 → `npm test --silent` 사용
     - `Cargo.toml` 발견 → `cargo build --quiet` 사용
     - `go.mod` 발견 → `go build ./...` 사용
   - 어느 것도 매치 안 되면 기존처럼 skip + info 로그.
2. 새 CLI flag `--no-smoke-test` (env: `RALPH_NO_SMOKE_TEST=true`):
   - true면 자동 추론과 명시 지정 모두 무시하고 skip.
3. `workflow.smokeTest`가 명시 지정되어 있으면 그것을 우선 (사용자 의도 존중).
4. tasks.json 스키마와 README/README.ko.md 문서 갱신:
   "기본은 자동 smoke test (opt-out via `--no-smoke-test`)".
5. 단위/통합 테스트:
   - `ParallelExecutor`의 smoke 분기를 직접 호출 가능한 형태로 작은 helper 분리.
   - 임시 디렉터리에 `*.csproj`만 있는 경우 → `dotnet build` 명령이 선택되는지 검증
     (실제 실행은 모킹).

**verification:** `dotnet build Ralph/Ralph.csproj -nologo`

**modifiedFiles:**
- `Ralph/Services/ParallelExecutor.cs`
- `Ralph/Program.cs`
- `ralph-schema.json`
- `README.md`
- `README.ko.md`

---

## Feature 8 — ParallelExecutor의 batch transition integration test

**문제 파일:** `Ralph/Services/ParallelExecutor.cs` (1113줄, untested)

가장 회귀 위험이 큰 path가 직접 테스트되지 않음. 특히 partial failure 후
다음 batch 진입 로직(`RunParallelBatchAsync` → `failed` 분리 → 다음 iter)은
회귀 시 데이터 손실/비용 폭주 가능.

**수정:**
1. `Ralph.Tests/ParallelExecutorTests.cs` 신규:
   - `MockAgentRunner` (Feature 2 산출) + 실제 `GitFixture` 사용.
   - 테스트 1: 2개 독립 task batch, 모두 성공 → 두 branch 머지 + tasks done.
   - 테스트 2: batch 2개 task 중 1개 실패 → 실패한 worktree만 정리되고 성공한
     쪽은 머지. 다음 iter에서 실패 task의 의존자가 차단되는지.
   - 테스트 3: smoke test 실패 시 종료 코드와 worktree 정리 동작 검증
     (Feature 7과 호환).
2. `IAgentRunner` 주입 경로를 `ParallelExecutor` 생성자에서 한 번 더 검토 —
   현재 이미 IAgentRunner를 받지만 테스트용 분리 가능 여부 확인.
3. CI 로컬 실행 안정성: 테스트 임시 dir은 `Path.GetTempPath()`에 격리.

**dependsOn:** Feature 2 (`MockAgentRunner`), Feature 7 (smoke 동작이 default opt-out으로 바뀐 뒤 검증).

**verification:** `dotnet test --filter "FullyQualifiedName~ParallelExecutor" -nologo`

**modifiedFiles:**
- `Ralph.Tests/ParallelExecutorTests.cs`

---

## 의존 그래프 요약

```
Feature 1 (CostTracker normalize) ──┐
                                    ├──► Feature 5 (CostTracker summary)
                                    └─ (CostTracker.cs 직렬화)

Feature 2 (MockAgentRunner) ────┬──► Feature 3 (LlmCritic uses Mock in tests)
                                └──► Feature 8 (ParallelExecutor tests use Mock)

Feature 7 (smoke opt-out) ──────────► Feature 8 (테스트가 새 동작 검증)

Feature 4 (worktree --shared)  — 독립
Feature 6 (README case study)  — 독립
```

병렬 가능 layer 예상:
- **Layer 1**: Feature 1, Feature 2, Feature 4, Feature 6, Feature 7 (5개 동시)
- **Layer 2**: Feature 3, Feature 5 (2개 동시)
- **Layer 3**: Feature 8 (단독)

## 안전 가드

- `tasks.json` 자체를 수정하는 task가 없도록 검증 (modifiedFiles에 포함되면 안 됨).
- `Program.cs`는 Feature 3, 4, 7이 모두 건드리므로 worktree 머지 시 충돌 가능 —
  머지 순서는 ralph가 자동 결정. 충돌 시 `conflictStrategies: ["auto-theirs", "claude"]`로
  해결되도록 workflow 설정에 의존.
- `README.md`/`README.ko.md`도 Feature 3, 4, 6, 7이 동시 수정 — 같은 이유.
  필요시 ralph가 plan을 만들면서 layer를 쪼갤 수 있다.
