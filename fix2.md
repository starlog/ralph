# Ralph 코드베이스 개선 사항 (Fix 2차)

본 문서는 fix1.md 작업 완료 후 추가 분석에서 발견된 항목을 우선순위 순으로 정리한 작업 명세입니다.
fix1과 동일하게 각 항목은 독립 실행 가능하며, 위에서부터 처리하는 것을 권장합니다.

---

## 1. [P1] CostTracker 정적 상태 → 인스턴스화 + DI

### 문제
`Ralph/Services/CostTracker.cs`가 프로세스 전역 정적 필드(`_cumulativeUsd`,
`_hydrated`, `_logDirOverride`, `Pricing`)를 사용한다. `ResetForTesting()`이
존재한다는 사실 자체가 설계 냄새이며, 다음 부작용을 유발한다.

- 테스트 격리 불가: 한 테스트의 누적값이 다음 테스트로 누설.
- 동일 워킹 디렉토리에서 여러 ralph 프로세스가 동시 실행될 경우 cache가
  서로의 변화를 인지하지 못함 (각자 자기 메모리만 신뢰).
- DI 컨테이너 도입 시 라이프사이클 충돌.

### 요구사항
- `CostTracker`를 인스턴스 클래스로 전환하고 `CommandContext`를 통해 주입.
- 정적 진입점을 유지해야 하는 경우(레거시 호출), 인스턴스에 위임하는 얇은
  static facade만 남김.
- `ResetForTesting()`은 인스턴스 dispose / 재생성으로 대체.
- 누적값은 `cost.jsonl`에서 hydration하므로 인스턴스 단위 캐시여도 정합성 유지됨을 확인.

### 검증
- 기존 `CostTracker` 단위 테스트가 인스턴스 기반으로 통과.
- 두 개의 `CostTracker` 인스턴스가 서로의 누적값에 영향을 주지 않는 격리 테스트.
- `--cost` 명령 출력값이 hydration 후 동일한지 회귀 테스트.

### 영향 파일
- `Ralph/Services/CostTracker.cs`
- `Ralph/Commands/CommandContext.cs`
- `Ralph/Commands/CostCommand.cs`
- 테스트: `Ralph.Tests/CostTrackerTests.cs`

---

## 2. [P1] Cost 기록 실패 시 별도 failures 로그

### 문제
`Ralph/Services/CostTracker.cs:101-121`의 `RecordAsync`가 5초 타임아웃으로
`cost.jsonl` append를 시도하고, 타임아웃/IO 실패 시 콘솔 경고만 출력하고
**조용히 드롭**한다. 결과적으로:

- 다음 `--cost` 명령이 underestimate 값을 보고.
- `--budget-usd` 게이트가 예상보다 늦게 트리거되어 예산 초과.
- 사후 디버깅 시 어떤 호출이 누락됐는지 추적 불가.

### 요구사항
- cost 기록 실패 시 `.ralph-logs/cost-failures.jsonl`에 fallback 기록:
  ```jsonc
  { "ts": "...", "taskId": "...", "model": "...", "usd": 0.0123,
    "reason": "timeout|io|...", "exception": "..." }
  ```
- 실패 카운트를 세션 종료 시 요약에 포함 ("cost ledger writes failed: N").
- fallback 기록도 실패하면 stderr에 명확히 출력 (silent drop 금지).
- 재시도는 짧게 1회만 (5초 → 200ms 백오프 → 포기).

### 검증
- `cost.jsonl` 경로를 read-only 디렉토리로 만들어 강제 실패 → `cost-failures.jsonl`
  생성 확인.
- 세션 요약에 실패 카운트가 표시되는지 확인.

### 영향 파일
- `Ralph/Services/CostTracker.cs`
- `Ralph/Services/RalphPaths.cs` (`CostFailuresLedger` 상수 추가)

---

## 3. [P1] verification.command 셸 인젝션 표면 축소

### 문제
`Ralph/Services/VerificationRunner.cs`의 `BuildShellPsi`가 `verification.command`
문자열을 그대로 `/bin/sh -c "..."` (POSIX) 또는 `cmd.exe /c "..."` (Windows)에
넘긴다. 명령은 Claude가 생성한 `tasks.json`에서 오므로 1차적으로는
신뢰 경계 안이지만, 다음 시나리오에서 문제가 된다.

- Claude가 의도치 않게 위험한 평가 문자열을 생성 (예: `eval`, 경로 치환 매크로).
- PRD/외부 입력이 task 프롬프트에 끼어들어 Claude가 생성한 검증 명령에 반영.
- `PlanValidator`는 일부 패턴(다중행 eval)만 경고하고 차단은 안 함.

### 요구사항
- `PlanValidator`에 `verification.command` 정적 검사 강화:
  - `eval`, `exec`, `$(...)` 내 외부 변수 치환, backtick, 그리고 `>` / `>>`로
    sensitive 경로(`.env`, `.ssh`, `~`) 쓰는 패턴 차단.
  - 차단 시 `errors`로 분류 (현재 `warnings` 수준이면 승격).
- 가능하면 명령을 단일 라인 + 화이트리스트 도구(`dotnet`, `npm`, `cargo`,
  `go`, `pytest`, `bash -c "..."` 등)로 시작하도록 `info` 수준 권장.
- 우회 의도가 명백한 경우(파이프로 다운로드 후 실행 등) 즉시 중단.

### 검증
- `Ralph.Tests/PlanValidatorTests.cs`에 위험 패턴 케이스 5종 이상 추가:
  - `curl ... | sh`
  - `eval $(cat /etc/passwd)`
  - `> ~/.ssh/authorized_keys`
  - 다중행 heredoc with `$(...)`
  - 환경변수 ($USER, $HOME)를 통한 경로 escape

### 영향 파일
- `Ralph/Services/PlanValidator.cs`
- `Ralph.Tests/PlanValidatorTests.cs`

---

## 4. [P1] Worktree 브랜치 삭제 가드 이중화

### 문제
`Ralph/Services/WorktreeService.cs`의 브랜치 삭제 가드는 `branch.{name}.ralphManaged`
config 키가 설정되어 있는지로만 판단한다. 다음 케이스에서 사용자 브랜치가
삭제될 위험이 남는다.

- config가 외부 도구(`git config --remove-section`, repo 복제)로 손실.
- 사용자가 실수로 `ralph/feature-x` 형식의 자기 브랜치를 만든 경우.
- repo가 fresh clone이라 config는 없지만 브랜치명은 패턴 일치.

### 요구사항
- 삭제 전 **모든** 다음 조건이 충족되어야:
  1. `branch.{name}.ralphManaged=true` config 존재 (현재 조건).
  2. **OR** 해당 브랜치가 `.ralph-worktrees/` 하위 워크트리에 현재 바인딩됨.
  3. **AND** 브랜치 reflog의 첫 entry가 ralph가 만든 commit (식별 가능한 경우).
- 위 조건 중 하나라도 불확실하면 삭제 보류 + 사용자 안내 메시지:
  > "브랜치 X는 ralph 표시는 있으나 안전 검증 실패. 수동 삭제 필요"
- 새 워크트리 생성 시 ralphManaged 외에 commit trailer 또는
  `.ralph-worktrees/{taskId}/.ralph-marker` 파일을 함께 생성하여 보강.

### 검증
- ralphManaged config가 있지만 워크트리 디렉토리가 없는 브랜치 → 삭제 보류 확인.
- 사용자가 만든 `ralph/test` 브랜치 (config 없음) → 삭제 안 됨 확인.

### 영향 파일
- `Ralph/Services/WorktreeService.cs`
- 테스트: `Ralph.Tests/WorktreeServiceTests.cs`

---

## 5. [P2] Rebase-advance 충돌 시 명확한 처리

### 문제
`Ralph/Services/WorktreeService.AdvanceWorktreeOntoBaseAsync`(머지 직전 rebase)가
충돌을 만났을 때의 동작이 명세화되어 있지 않다. 실패 시 다음이 모호:

- 다른 batch task의 워크트리는 영향 없는가?
- 사용자가 수동 개입할 때 rebase 중간 상태가 워크트리에 남는가?
- 실패한 task만 abort하고 batch는 계속 가는가, 전체 abort인가?

### 요구사항
- rebase 충돌 발생 시:
  - `git rebase --abort`로 워크트리를 깨끗한 상태로 복구.
  - 해당 task만 실패 처리 (`MergeFailureKind.RebaseConflict`).
  - batch의 다른 독립 task는 계속 진행.
  - 사용자에게 `ralph --task {id} --force` 또는 수동 머지 안내.
- conflict 파일 목록을 stderr에 출력 (locale-safe 방식 — fix1 #3과 일관).
- `MergeOrchestrator`의 `conflictStrategies` 체인이 rebase 단계에서도
  적용되는지 검토 (현재는 merge 단계에만 적용된 듯).

### 검증
- 두 task가 같은 파일을 수정하도록 인위적으로 만든 후 rebase advance에서
  충돌 → 한 task만 실패하고 다른 task는 진행되는지 확인.

### 영향 파일
- `Ralph/Services/WorktreeService.cs`
- `Ralph/Services/MergeOrchestrator.cs`

---

## 6. [P2] 대형 PRD에 대한 PlanGenerator 청킹 전략

### 문제
`Ralph/Services/PlanGenerator.cs`가 PRD 전문 + JSON Schema + 시스템 프롬프트를
**단일 LLM 호출**로 전송한다. PRD가 커지면 (>50KB) 다음 위험:

- opus context window는 충분하지만 출력 토큰 한계로 tasks.json이 잘림.
- atomic write는 가능하지만 잘린 JSON은 schema validation에서 실패.
- 자동 correction loop(2회)도 동일 문제로 반복 실패.

### 요구사항
- PRD/예상 출력 크기 추정 (토큰 카운트):
  - 임계치 미만 → 현재 방식.
  - 초과 시 2단계 전략:
    1. **개요 단계**: PRD → "주요 기능 영역 + 각 영역의 task 개수 + 의존 관계"
       (요약 JSON).
    2. **상세 단계**: 영역별로 task를 생성, 마지막에 병합 + 의존 그래프 검증.
- 또는 더 단순한 fallback: 출력 토큰 한계를 감지하면(stop_reason=length)
  사용자에게 "PRD를 N개 섹션으로 나눠 다시 시도하라" 안내 + sample 분할 가이드.
- `--plan-prompt`에서도 청킹 전략을 시각화 (어떻게 분할되는지 미리보기).

### 검증
- 100KB 이상 PRD 샘플로 plan 생성 → 잘림 없이 완전한 tasks.json 산출.
- 청킹 활성화 시 최종 그래프가 단일 호출 결과와 의미적으로 동등한지
  스냅샷 비교.

### 영향 파일
- `Ralph/Services/PlanGenerator.cs`
- 신규: `Ralph/Services/PlanChunker.cs` (택1)

---

## 7. [P2] 머지 후 per-batch 자동 롤백 옵션

### 문제
현재 `RollbackService`는 `--plan` 시점만 스냅샷을 잡는다. `--run` 도중
머지된 task는 `state.json` reset만으로는 되돌릴 수 없고, smoke test 실패 시
사용자가 수동으로 git revert해야 한다.

`--strict-files`와 `workflow.smokeTest`가 방어선이지만, smoke test 실패
**이후의** 자동 복구 옵션이 없어 다음 batch가 깨진 base 위에 빌드된다.

### 요구사항
- 새 옵션 `--auto-rollback-on-smoke-fail` (또는 workflow 설정):
  - smoke test 실패 시 해당 batch의 모든 머지 커밋을 자동으로 revert.
  - revert 커밋 메시지에 실패 사유 + smoke test 출력 포함.
  - state.json에서 해당 task들을 다시 pending으로 표시.
- 기본값은 off (현재 동작 유지) — opt-in.
- `RollbackService`에 batch-level 스냅샷 추가:
  - 각 batch 시작 전 base SHA 기록.
  - 실패 시 `git reset --hard {snapshot-sha}` (사용자 작업 없을 때만 안전).

### 검증
- 의도적으로 깨진 task를 머지하는 시나리오 → smoke 실패 → 자동 revert
  → state.json 일관성 → 다음 `--run`이 같은 task 재실행.

### 영향 파일
- `Ralph/Services/RollbackService.cs`
- `Ralph/Services/MergeOrchestrator.cs`
- `Ralph/Services/SmokeTestPlanner.cs`
- `Ralph/Commands/RunCommand.cs`

---

## 8. [P3] 머지 트랜잭션 로그

### 문제
fix1 #1로 done-mark 실패 시 batch abort가 구현되었지만, **이미 머지된
커밋 SHA**와 **state.json 마킹**의 관계가 디스크에 명시적으로 남지 않는다.
사후 복구 시 "어느 SHA가 어느 task인가"를 git log + 커밋 메시지로 추정해야 함.

### 요구사항
- `.ralph-logs/merge-log.jsonl`에 batch별 머지 결과 append:
  ```jsonc
  { "ts": "...", "batch": 3, "taskId": "feature-x-impl",
    "baseSha": "...", "mergedSha": "...", "stateMarked": true,
    "smokeTest": "passed|failed|skipped" }
  ```
- 한 task에 대해 entry는 **idempotent하게 1회만** (재실행 시 중복 없음).
- `--status`가 이 로그를 읽어 진행 상황을 더 정확히 표시.
- `--rollback`이 이 로그를 활용하여 정밀 복구 (현재는 전체 스냅샷 복원).

### 검증
- batch 실행 → merge-log.jsonl 검사 → entry 무결성 확인.
- 동일 task를 두 번 실행해도 entry 중복 없음 확인.

### 영향 파일
- `Ralph/Services/MergeOrchestrator.cs`
- `Ralph/Services/RollbackService.cs`
- `Ralph/Commands/StatusCommand.cs`
- `Ralph/Services/RalphPaths.cs` (`MergeLog` 상수 추가)

---

## 9. [P3] `--dangerously-skip-permissions` 사용 명시화

### 문제
`Ralph/Services/ClaudeService.cs`가 모든 Claude 호출에 `--dangerously-skip-permissions`
를 항상 붙인다. 워크트리 격리 환경이라는 정당화는 가능하나:

- 워크트리도 호스트 파일시스템과 동일 권한.
- README/CLAUDE.md에 "이 플래그를 늘 사용한다"는 명시가 약함.
- 의식적으로 켜진 게 아닌 사용자(설치 후 무지성 실행)는 이를 인지 못 함.

### 요구사항
- `README.md` / `README.en.md`의 보안 섹션에 다음 명시:
  - Ralph는 항상 `--dangerously-skip-permissions`로 Claude를 실행함.
  - 이는 격리된 워크트리에서 수행되지만 호스트 FS 접근 가능함을 인지해야 함.
  - 민감한 환경에서는 별도 컨테이너/VM에서 실행 권장.
- 새 옵트아웃 옵션 `--safe-permissions` 검토:
  - Claude가 권한 요청을 띄우는 표준 모드.
  - 자동화 흐름에서는 비실용적이지만 한 번의 plan 생성 같은 일회성 작업에 유용.
- 환경변수 `RALPH_REQUIRE_PERMISSIONS=true` 설정 시 자동으로 safe 모드.

### 검증
- README diff 리뷰 (사람 검토).
- `--safe-permissions` 플래그 단위 테스트 (Claude args 구성 확인).

### 영향 파일
- `README.md`, `README.en.md`
- `Ralph/Services/ClaudeService.cs`
- `Ralph/Commands/ArgParser.cs`

---

## 작업 순서 권장

1. **#1, #2, #3, #4**를 먼저 처리 (P1 — 운영/보안 신뢰성 직결).
2. **#5, #6, #7**을 병행 (P2 — 견고성/확장성). #6은 큰 PRD 사례가 늘어나기 전에 선제.
3. **#8, #9** (P3 — 관측성 + 문서/UX).

각 항목 완료 시 별도 PR로 분리하고, 커밋 메시지는 한국어로 작성 (CLAUDE.md 규칙).

---

## 분석 메타데이터

- 분석 대상 버전: v1.32 (commit 3f82e41 기준, fix1 전체 머지 완료 후)
- 분석 일자: 2026-04-30
- 선행 문서: `fix1.md` (fix1 #1~#8 모두 머지됨 — 본 문서는 그 이후 잔여 이슈)
- 주요 검토 파일: CostTracker, VerificationRunner, WorktreeService,
  PlanGenerator, MergeOrchestrator, RollbackService, ClaudeService, PlanValidator
