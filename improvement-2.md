# Ralph 개선 계획 v2 (PRD)

> 본 문서는 `ralph --plan improvement-2.md`로 처리되어 ralph 자체가 실행할 수
> 있도록 작성된 PRD다. 각 Feature는 독립적으로 구현 가능하며, modifiedFiles가
> 겹치지 않게 설계되어 병렬 실행이 가능하다.

## 배경

`improvement.md`(원안)와 `improvement-1.md`(보완안)의 P0~P2 항목 대부분이 직전
릴리스로 반영되었다(commit `ef5bec7`). 그러나 다음 잔여 결함과 신규 기능이
남아 있다:

1. 단일 task 실행이 의존성을 무시한다 (`--task <id>`)
2. worktree 안의 Claude가 `tasks.json`을 수정하면 머지 충돌이 발생할 수 있다
3. `CostTracker.RecordAsync`가 병렬 경로(`ParallelExecutor`)에서만 호출되고,
   순차 실행 경로(`RunAutoLoop`, `--task`)는 비용을 기록하지 않는다
4. `modifiedFiles` 정확성을 머지 후에 실측 검증하지 않는다
5. 사용 비용에 대한 budget cap이 없어 AFK 실행 중 무제한 소진 가능
6. README가 한국어 전용이고 보안 주의 경고가 없다

본 문서는 위 6개 항목을 6개 Feature로 정의한다. 각 Feature는 plan/impl/test/commit
4단계로 분해 가능하나, 변경 범위가 작은 항목은 1~2 task로 축약될 수 있다
(`PlanGenerator`의 granularity 가이드 참조).

## 우선순위

- **P0** — 현재 코드의 결함 수정. 작은 변경, 큰 안전성 효과.
- **P1** — 병렬 실행 안전성과 비용 통제 신규 기능.
- **P2** — 사용자 채택과 보안 인지.

---

## Feature 1 (P0): `--task` 의존성 검사 + `--force` 분리

### 문제

현재 `ralph --task <id>`는 `dependsOn`이 미완료 상태여도 경고 없이 실행한다.
디버깅용 강제 실행은 명시적이어야 하는데, 기본 동작이 silent override다.
이러면 의존 산출물 없이 실행된 task가 실패하고도 이유를 알기 어렵다.

### 요구사항

- `--task <id>` 실행 시점에 `task.DependsOn`을 순회해 미완료 의존성을 출력한다
- 미완료 의존성이 있으면 사용자 확인 prompt를 띄운다 (interactive TTY일 때)
- non-TTY(CI 등)거나 `--force` 플래그가 있으면 검사를 건너뛴다
- 미완료 의존성 목록은 task ID와 현재 상태(pending/failed)를 함께 표시

### 변경 대상 파일

- `Ralph/Program.cs` — `HandleSingleTask` 함수에 dependency check 추가

### 수용 기준

1. `ralph --task X`에서 X의 의존이 미완료면 프롬프트 표시되고 N 입력 시 종료
2. `ralph --task X --force`는 검사 건너뛰고 즉시 실행
3. 의존이 모두 완료된 경우 추가 프롬프트 없이 진행
4. 비대화형 환경(`Console.IsInputRedirected`)에서는 `--force` 없으면 exit 1
5. 기존 의존성 모두 완료된 정상 케이스 동작 회귀 없음

---

## Feature 2 (P0): worktree 안 `tasks.json` 쓰기 방어

### 문제

병렬 worktree에서 도는 Claude가 자신의 worktree에 있는 `tasks.json`을 수정할
경우 (사용자 prompt가 명시적으로 수정을 지시하지 않더라도, 잘못된 추론으로
수정 가능):

1. 각 worktree마다 다른 버전의 `tasks.json`이 생성된다
2. 머지 시 거의 확실한 충돌
3. base의 ralph가 갱신한 done 상태가 worktree의 옛 `tasks.json`으로
   덮어씌워질 위험

`PromptBuilder`가 "tasks.json 수정 금지"를 명시하고 있으나 prompt 차원의
가드만으로는 부족하다. 검증과 머지 전략으로 방어를 다중화한다.

### 요구사항

- worktree 작업 종료 후, base에 머지하기 전 검증:
  - `git diff --name-only HEAD ralph/{taskId}` 결과에 `tasks.json`이 포함되면
    경고 로그 + worktree 내에서 강제 revert (`git checkout HEAD~1 -- tasks.json`)
- 머지 전략: `tasks.json`은 ours 우선
  - 머지 명령에 `-X ours` 대신, 머지 전 worktree의 `tasks.json`을 base 버전으로
    덮어쓰기 (단순하고 명시적)
- 검출 시 `RalphLogger.Warn`으로 사고 기록
- `.ralph-logs/`에 누적 위반 카운트 (선택)

### 변경 대상 파일

- `Ralph/Services/WorktreeService.cs` — 머지 전 `tasks.json` 정규화 메서드 추가
- `Ralph/Services/ParallelExecutor.cs` — 머지 호출부에서 정규화 호출

### 수용 기준

1. worktree에서 `tasks.json`이 수정된 상태로 머지를 시도해도 충돌 없이 머지됨
2. 위반이 감지되면 RalphLogger에 경고 라인 기록
3. 정상 worktree(=`tasks.json` 미수정)는 영향 없음
4. 머지 후 base의 `tasks.json`은 ralph가 직접 갱신한 최신 상태 유지

---

## Feature 3 (P0): 순차 실행 경로의 비용 기록 통일

### 문제

`CostTracker.RecordAsync`는 `ParallelExecutor`의 3개 호출 지점에서만 발화한다.
순차 실행 경로인 `RunAutoLoop`(Program.cs:1084)와 단일 task 경로
(Program.cs:915)는 `claude.RunWithRetryAsync`만 호출하고 비용 기록을 누락한다.

결과적으로 `ralph --run --sequential` 또는 `ralph --task X`로 돌린 세션은
`--cost` 출력에 나타나지 않아 누적 비용이 과소 보고된다.

### 요구사항

- `RunAutoLoop` 안에서 `RunWithRetryAsync` 직후 `CostTracker.RecordAsync` 호출
- `HandleSingleTask`도 동일
- `HandleInteractive`(`Program.cs:1084`)도 동일
- task ID와 model을 정확히 전달 (model이 null이면 "opus" 기본)

### 변경 대상 파일

- `Ralph/Program.cs` — 위 3개 호출 지점에 cost 기록 추가

### 수용 기준

1. `ralph --run --sequential` 후 `ralph --cost`가 0이 아닌 누적값 표시
2. `ralph --task X` 한 번 실행 후 `--cost`에 해당 task가 1줄 추가됨
3. interactive 모드에서 task 실행 후 `--cost`에 반영됨
4. 병렬 경로 비용 기록 회귀 없음 (기존 entry 형식과 동일)

---

## Feature 4 (P1): `modifiedFiles` 머지 후 실측 검증

### 문제

병렬 worktree merge의 안전성은 `modifiedFiles` 정확성에 100% 의존한다.
PRD에서 plan generator가 추론한 `modifiedFiles`가 부정확하면 silent overwrite
위험이 있다. `PlanValidator`가 사전 overlap 검사는 하지만, 실제로 수정된
파일과 declared 사이의 괴리는 검증하지 않는다.

### 요구사항

- 각 worktree에서 task 실행 완료 후, 머지 직전:
  - `git diff --name-only base..HEAD` (worktree 기준)으로 실제 변경 파일 수집
  - `task.ModifiedFiles ∪ task.OutputFiles`와 비교
  - **Undeclared** = 실제 변경 - declared
  - **NotChanged** = declared - 실제 변경
- `Undeclared`가 비어있지 않으면 RalphLogger.Warn으로 기록
- `.ralph-logs/validation.jsonl`에 다음 형식으로 누적:
  ```json
  {"taskId": "...", "timestamp": "...", "declared": [...], "actual": [...],
   "undeclared": [...], "notChanged": [...]}
  ```
- 기본 동작은 warn-only (머지 진행). `--strict-files` 플래그가 있으면
  `Undeclared` 비어있지 않을 때 머지 중단 + task 실패 처리

### 변경 대상 파일

- `Ralph/Services/WorktreeService.cs` — 검증 메서드 추가
- `Ralph/Services/ParallelExecutor.cs` — 머지 직전 검증 호출
- `Ralph/Program.cs` — `--strict-files` 플래그 처리

### 수용 기준

1. 의도적으로 declared에 빠진 파일을 수정하는 task를 만들면 warn 로그 발생
2. `.ralph-logs/validation.jsonl`에 1줄 추가됨
3. `--strict-files` 시 해당 task가 failed로 표시되고 머지 안 됨
4. declared와 실제가 정확히 일치하는 task는 로그 없이 정상 머지
5. 기존 정상 케이스 회귀 없음

---

## Feature 5 (P1): Budget gate (`--budget-usd`)

### 문제

`CostTracker`가 누적 기록은 하지만 임계값 도달 시 graceful pause가 없다.
`maxConcurrent: 16` × 다수 feature × 4 phases면 Claude Max weekly limit를
순식간에 소진할 수 있다. AFK 실행이 실제로 어디까지 진행됐는지 모르고
limit 초과로 멈춘 상태가 가능하다.

### 요구사항

- `ralph --run --budget-usd <amount>` 플래그 추가
- 각 task 시작 직전 누적 비용을 `cost.jsonl`에서 합산해 임계값 비교
  - 누적 ≥ 80% → 경고 출력 (한 번만)
  - 누적 ≥ 100% → 현재 진행 중인 task는 완료 대기, 새 task 시작 안 함
    → "budget reached" 메시지 + 다음 실행을 위한 안내 출력 후 종료
- 환경변수 `RALPH_BUDGET_USD`로도 설정 가능 (CLI 우선)
- Webhook이 설정되어 있으면 budget reached 이벤트도 별도 알림 (선택)

### 변경 대상 파일

- `Ralph/Services/CostTracker.cs` — `GetTotalUsdAsync()` 메서드 추가
- `Ralph/Services/ParallelExecutor.cs` — task 시작 직전 budget check
- `Ralph/Program.cs` — `--budget-usd` 플래그 처리, `RunAutoLoop`에도 동일 적용

### 수용 기준

1. `ralph --run --budget-usd 0.001` 시 첫 task 후 즉시 budget reached로 종료
2. 충분히 큰 임계값에서는 정상 종료
3. 80% 도달 시 경고가 정확히 한 번만 출력됨 (중복 없음)
4. 종료 코드는 budget reached 시 0이 아닌 값(예: 2)으로 구별
5. 진행 중이던 task는 강제 종료되지 않고 끝까지 완료

---

## Feature 6 (P2): README 보안 경고 + 영문판 + GitHub topics

### 문제

- README가 한국어 전용이라 글로벌 채택이 어렵다
- 차별화 포인트(병렬 worktree)가 README 상단에 강조되어 있지 않다
- Claude Code를 host에서 직접 실행한다는 보안 경고가 없다 (`~/.ssh`, `~/.aws`,
  환경변수 노출 가능)
- 검색 노출용 GitHub topics가 부족하다

### 요구사항

#### 6-1. 영문 README

- `README.md`를 영문으로 재작성
- 기존 한국어 내용은 `README.ko.md`로 이동
- 첫 문단에서 차별화 포인트 강조:
  > The first Ralph implementation with **parallel git worktree execution**.
  > Run multiple Claude Code agents simultaneously on independent features,
  > with automatic dependency resolution, conflict-aware merging, and live
  > progress monitoring.

#### 6-2. 보안 경고 섹션

`README.md`와 `README.ko.md` 모두에 다음 섹션 추가:

```markdown
## ⚠️ Security Note

Ralph runs Claude Code directly on the host machine. Untrusted PRDs or
external `tasks.json` files should be executed in an isolated environment
(separate user account, VM, or container). The following may be exposed:

- Credentials in ~/.ssh, ~/.aws, ~/.config
- API keys in environment variables
- Read access to all host files
```

#### 6-3. 비교 표

타 Ralph 구현체와의 차별점을 표로:

| Feature | snarktank/ralph | PageAI/ralph-loop | starlog/ralph |
|---------|----------------|-------------------|---------------|
| Parallel execution | ❌ | ❌ | ✅ |
| Windows support | ❌ | ❌ | ✅ |
| DAG dependencies | ❌ | partial | ✅ |
| Cost tracking | ❌ | ❌ | ✅ |
| Single binary | ❌ | ❌ | ✅ |

#### 6-4. GitHub topics

`.github/topics.txt` 같은 파일은 안 되니, 별도 commit 메시지로 사용자가
GitHub 웹에서 직접 추가하도록 안내한다. 추천 topics:
`ralph-loop`, `agentic-ai`, `ai-coding`, `prd`, `task-orchestrator`,
`claude-code`, `autonomous-agent`, `parallel-execution`

→ 이 항목은 docs/CONTRIBUTING.md 또는 README 하단 "GitHub Topics"
   섹션에 텍스트로만 추가하고, 실제 적용은 사용자(repo 소유자)가 수행한다.

### 변경 대상 파일

- `README.md` — 영문 재작성 + 보안 경고 + 비교 표
- `README.ko.md` — 기존 README.md 내용 이동 + 보안 경고
- 단, install-binary.sh / 기타 코드 변경 없음 (문서만)

### 수용 기준

1. `README.md`의 첫 100줄 안에 "parallel git worktree execution" 문구 포함
2. `README.md`와 `README.ko.md` 모두 "Security Note" / "보안 주의" 섹션 보유
3. 비교 표가 README.md 안에 렌더링됨
4. 기존 한국어 사용 가이드는 `README.ko.md`로 보존되어 있음
5. 모든 내부 링크 깨지지 않음 (install-binary.sh 등)

---

## Workflow 설정 권장

본 PRD는 다음 설정으로 실행하기를 권장한다:

```json
{
  "workflow": {
    "onTaskComplete": {
      "commitChanges": true
    },
    "parallel": {
      "enabled": true,
      "maxConcurrent": 6,
      "conflictStrategy": "claude"
    },
    "logRetentionDays": 30
  }
}
```

### 의존성 그래프 (예상)

Feature 1~6은 변경 대상 파일이 거의 겹치지 않아 대부분 병렬 실행 가능하다:

- Feature 3과 Feature 5는 모두 `Ralph/Program.cs`와 `CostTracker.cs`를
  수정하므로 직렬화 필요 (Feature 3 → Feature 5 순서 권장)
- Feature 1과 Feature 4는 `Program.cs`를 모두 수정하므로 직렬화 필요
- Feature 2와 Feature 4는 `WorktreeService.cs`와 `ParallelExecutor.cs`를
  공유하므로 직렬화 필요 (Feature 2 → Feature 4 순서 권장)

병렬 가능 batch 예시:
- Batch 1: Feature 1, Feature 2, Feature 6 (서로 다른 파일군)
- Batch 2: Feature 3 (Feature 1과 Program.cs 충돌하므로 다음 batch)
- Batch 3: Feature 4 (Feature 2, Feature 3과 충돌)
- Batch 4: Feature 5 (Feature 3 위에 얹음)

`ralph --plan improvement-2.md` 시 plan generator가 위 의존성을 추론하도록
각 Feature의 "변경 대상 파일" 섹션을 정확히 기재했다.

### 검증 방법

각 Feature 구현 후 `ralph --validate`로 PlanValidator가 깨끗한지 확인하고,
`dotnet build Ralph/Ralph.csproj`로 컴파일 확인한다. 통합 테스트:

```bash
# Feature 1
ralph --task <some-id-with-pending-deps>   # 프롬프트 떠야 함
ralph --task <id> --force                   # 즉시 실행

# Feature 3
ralph --run --sequential                    # 종료 후
ralph --cost                                # 비용 표시 확인

# Feature 5
ralph --run --budget-usd 0.001              # 즉시 budget reached
```

---

## Out of Scope

본 PRD는 다음을 포함하지 않는다 (별도 PRD로 분리):

- Verification gate (`improvement.md` P0-2의 본격 구현 — 별도 PRD 필요)
- Crash recovery / atomic writes / heartbeat (`improvement.md` P1-4)
- Learning loop / AGENTS.md 자동 업데이트 (`improvement.md` P0-1) — ROI 불확실
- Docker sandbox (`improvement.md` P1-3) — 사용자 요청 누적 후
- Plan iteration UI (`improvement.md` P2-2)

위 항목들은 본 PRD의 6개 Feature가 완료된 후 사용자 피드백을 보고 결정한다.
