# Ralph 아키텍처 로드맵 (Future Plan)

본 문서는 `fix1.md` / `fix2.md`의 전술적 수정과는 별개로, Ralph가 v2.x로
넘어갈 때 검토할 **구조적 변화**를 우선순위 + 트레이드오프와 함께 정리합니다.

전술 수정(fix*.md)은 "지금 깨진 것을 고친다"이고, 본 문서는 "다음 단계의
형태를 결정한다"입니다. 따라서 각 항목은 **언제 시작해야 하는지의 신호**와
**얻는 것 / 잃는 것**을 함께 명시합니다.

---

## 우선순위 요약

| # | 항목 | 우선순위 | 작업량 | 시작 신호 |
|---|---|---|---|---|
| 1 | 이벤트 버스 도입 | **High** | 1주 | 지금 시작해도 좋음 |
| 2 | 컨테이너 격리 (`IIsolationProvider`) | High | 2~3주 | 보안 사용 사례 등장 |
| 3 | 명령 진입점 분리 (`ralph-plan` / `ralph-run`) | Medium | 3~5일 | CI에서 ralph 사용 요청 |
| 4 | SQLite 통합 ledger | **Deferred** | 1~2주 | jsonl 성능/정합성 한계 |
| 5 | 워크플로우 엔진 일반화 (sub-DAG, 조건 노드) | Deferred | 2~4주 | 4단계로 안 풀리는 PRD 3건+ |

---

## 1. [High] 이벤트 버스 도입

### 동기

현재 `MergeOrchestrator`, `ParallelExecutor`, `WorktreeTaskRunner`가
`AnsiConsole`, `RalphLogger`, `CostTracker`, `NotificationService`를
**직접 호출**한다. 결과적으로:

- 새 옵저버(예: OpenTelemetry, IDE 플러그인) 추가 시 코어 코드 수정 필요.
- 테스트가 사이드 이펙트 mocking으로 복잡 — 진짜 검증할 비즈니스 로직이
  부수 호출에 묻힘.
- 향후 항목 #2~#5가 모두 이 결합 위에 추가 결합을 쌓아야 함.

### 변경

가벼운 in-process pub/sub 이벤트 버스 도입.

```csharp
// Ralph/Events/RalphEvents.cs
public abstract record RalphEvent(DateTime At);
public record TaskStarted(string TaskId, string Model) : RalphEvent;
public record TaskCompleted(string TaskId, TimeSpan Duration, decimal CostUsd) : RalphEvent;
public record TaskFailed(string TaskId, string Reason) : RalphEvent;
public record MergeStarted(int Batch, string TaskId) : RalphEvent;
public record MergeCompleted(int Batch, string TaskId, string MergedSha) : RalphEvent;
public record MergeFailed(int Batch, string TaskId, MergeFailureKind Kind) : RalphEvent;
public record SmokeTestCompleted(int Batch, bool Passed, string Output) : RalphEvent;
public record CostRecorded(string TaskId, string Model, decimal UsageUsd) : RalphEvent;
```

옵저버를 분리:
- `ConsoleProgressReporter` — 라이브 대시보드 (Spectre.Console).
- `FileLogger` — 세션/task 로그 파일.
- `CostRecorder` — `cost.jsonl` 기록.
- `NotificationDispatcher` — Slack/Discord 웹훅.
- `MergeLogWriter` — fix2 #8 머지 트랜잭션 로그.

코어 서비스는 `IEventBus.Publish(...)`만 호출.

### 얻는 것

- **테스트가 단순화**: "이벤트 시퀀스가 [TaskStarted, MergeFailed, TaskFailed]
  순서로 발행되는가" 같은 선언적 검증.
- **확장점이 명확**: 새 통합(예: Linear 이슈 자동 생성, OTLP exporter)을
  코어 변경 없이 추가.
- **#2~#5가 자연스럽게 따라옴**: 격리 변경, 워크플로우 변경 모두
  이벤트 발행만 유지하면 호환.

### 잃는 것

- 이벤트 객체 정의 + 발행 코드 추가로 처음에 LOC 약간 증가.
- 잘못 쓰면 "어디서 발행되는지 모르겠는" 분산 로직 안티패턴.
  → **명령형 백본은 유지**하고 사이드 이펙트(로그/알림/메트릭)만 이벤트화.

### 시작 신호

지금. 단, **fix2 작업 완료 후**에 시작 (지금 fix2 작업 중이면 충돌).

### 영향 파일

- 신규: `Ralph/Events/` 디렉토리.
- 수정: `Ralph/Services/ParallelExecutor.cs`, `MergeOrchestrator.cs`,
  `WorktreeTaskRunner.cs`, `VerificationLoop.cs`.
- 옵저버 분리: 기존 `CostTracker`, `NotificationService`, `RalphLogger`를
  옵저버로 등록.

---

## 2. [High] 컨테이너 격리 — `IIsolationProvider`

### 동기

현재 격리 모델은 **워크트리 = 디렉토리 격리**. Claude는
`--dangerously-skip-permissions`로 실행되어 호스트 FS 전체 접근 가능:

- `~/.ssh`, `~/.aws/credentials`, 다른 repo의 `.env` 모두 읽힘.
- 워크트리에서 `cd ~ && rm -rf` 가 막히지 않음.
- 마케팅 단계에서 "대량 코드 자동화"를 강조하면 보안 검토를 통과해야 함.

### 변경

격리 단위를 추상화:

```csharp
public interface IIsolationProvider
{
    Task<IsolatedEnvironment> CreateAsync(string taskId, string baseRef, CancellationToken ct);
    Task DisposeAsync(IsolatedEnvironment env, CancellationToken ct);
}

public class WorktreeIsolation : IIsolationProvider { /* 현재 동작 */ }
public class PodmanIsolation : IIsolationProvider { /* 컨테이너 + 워크트리 마운트 */ }
public class LocalIsolation : IIsolationProvider { /* 격리 없음, CI 전용 */ }
```

`PodmanIsolation`의 mount 정책:
- 워크트리 디렉토리 → read-write.
- repo 루트(워크트리 외) → read-only.
- `~/.ssh`, `~/.aws`, `~/.gnupg` → 차단.
- 네트워크 → 옵션 (기본 차단, `--allow-network` opt-in).

### 얻는 것

- "Ralph는 안전하다"를 진심으로 말할 수 있음.
- `--dangerously-skip-permissions`의 위험 surface가 컨테이너 안에 갇힘.
- CI 환경(GitHub Actions, ephemeral VM)에서는 자동으로 `LocalIsolation` 선택
  가능 — 외부 격리가 이미 있으므로 중복 회피.

### 잃는 것

- **시작 오버헤드 +2~5초/태스크**. 50개 태스크 병렬 시 무시 못 함.
- Windows에서 Docker Desktop 또는 WSL2 의존.
- 디버깅 복잡도: 컨테이너 안의 실패는 `podman exec`로 들어가야 봄.
- Claude CLI를 컨테이너 안에 설치해야 함 (이미지 크기 + 인증 토큰 마운트).

### 시작 신호

다음 중 하나라도:
- 사용자가 보안 우려를 보고함.
- 엔터프라이즈/팀 사용 사례 등장.
- `--dangerously-skip-permissions` 관련 GitHub 이슈 발생.

### 설계 노트

- 처음에는 `--isolation=worktree` (기본) / `--isolation=podman` 두 옵션만.
- `IsolationProvider`는 #1 이벤트 버스 위에서 `IsolationCreated` /
  `IsolationDisposed` 이벤트 발행 → 대시보드/로그 자동 호환.

---

## 3. [Medium] 명령 진입점 분리

### 동기

현재 `ralph` 단일 바이너리가 모든 것을 함:
- PRD 분석 (Claude 필요)
- tasks.json 생성 (Claude 필요)
- 워크트리 + 머지 (git 필요)
- 상태 조회 (의존성 없음)
- 검증/critique (Claude 선택)

CI에서 `ralph status` 또는 `ralph validate`만 쓰고 싶어도 Claude CLI 의존성을
맞춰야 함. 컨테이너 이미지 크기 + 인증 설정 부담.

### 변경

같은 바이너리, 명확한 모드 분리 + 의존성 사전 검사 차별화:

```bash
ralph plan PRD.md          # 필수: claude, git
ralph run                   # 필수: claude (선택), git
ralph validate              # 필수: 없음
ralph status                # 필수: 없음
ralph cost                  # 필수: 없음
ralph rollback              # 필수: git
```

`DependencyChecker`가 명령별로 의존성 매트릭스를 가지도록 (이미 일부 구현됨 —
완전 분리 + 명시화).

선택적으로 빌드 타임에 **slim variant** (Claude 미포함, read-only 명령만):
```
ralph (full) — 30MB
ralph-readonly — 8MB (validate/status/cost/logs만)
```

### 얻는 것

- CI 컨테이너에 Claude 안 깔아도 됨.
- "ralph 결과 모니터링" 자동화가 가벼워짐.
- Homebrew/Scoop에서 두 가지 formula 제공 가능.

### 잃는 것

- 빌드 매트릭스 추가 (full + slim).
- 사용자가 어떤 variant를 깔지 결정해야 함 (대부분은 full로 충분).

### 시작 신호

- CI/CD 파이프라인에서 ralph 사용 요청.
- 관측성 도구(Grafana, Datadog)에서 `ralph status`를 cron으로 호출하고 싶다는 요구.

---

## 4. [Deferred] SQLite 통합 ledger

### 동기 (잠재적)

`state.json`, `cost.jsonl`, `validation.jsonl`, `merge-log.jsonl`(fix2 #8),
`cost-failures.jsonl`(fix2 #2)... ledger 종류가 늘고 있음. 각자 자기 락/원자성
규칙이 있어 사실상 **append-only DB를 손으로 만드는 중**.

### 변경 (만약 한다면)

`.ralph-logs/ralph.db` (SQLite, WAL 모드) 단일 저장소:

| 테이블 | 대체하는 파일 |
|---|---|
| `tasks` | (tasks.json은 그대로 유지 — 스펙은 git-tracked) |
| `task_runs` | `state.json`의 done 필드 |
| `cost_records` | `cost.jsonl` |
| `validation_log` | `validation.jsonl` |
| `merge_log` | (신규) |

**텍스트 로그는 그대로 유지** — `.ralph-logs/{taskId}.log`,
세션 로그는 tail/grep 대상이므로 SQLite로 옮기지 않음.

### 얻는 것

- 머지 + state 마킹을 **트랜잭션으로 진짜 원자화** (fix1 #1을 스토리지
  레벨에서 보장).
- `--status`, `--cost`, `ralph history` 쿼리가 SQL 한 줄.
- 여러 ralph 프로세스 동시 실행이 자연스럽게 안전 (WAL).
- ledger 추가 시 새 파일 + 락 정책이 아닌 새 테이블만 추가.

### 잃는 것 (이게 큼)

- **사람이 읽을 수 없음**. `cat cost.jsonl | jq`가 안 통함.
- `sqlite3` CLI 또는 `ralph cost --query "SELECT ..."` 같은 우회 필요.
- "이상한 비용 청구가 어디서 왔지?"를 30초만에 파악하던 게 명령어 한 단계 더 거침.
- 마이그레이션 코드 + 다운그레이드 경로 필요.

### 왜 deferred인가

- fix1 #1이 코드 레벨로 이미 패치됨 → 스토리지 트랜잭션이 *지금* 절실하지 않음.
- jsonl이 작을 때는 `jq`/grep이 SQL보다 빠르고 편함.
- 가시성 손실의 비용이 작업량(1~2주)에 비해 너무 큼.

### 시작 신호 (이 중 2개 이상 충족 시)

- `cost.jsonl`이 50MB 초과 → `--cost`가 5초 이상 걸림.
- 다중 ralph 프로세스 동시 실행 사례 등장.
- 새 ledger 종류가 2개 더 늘어남 (현재 4 → 6 이상).
- 사용자가 "ralph history" 같은 시계열 쿼리 요구.

### 마이그레이션 시 호환성

- `ralph export --format jsonl` 명령으로 SQLite → 기존 jsonl 포맷 export 가능.
- 첫 실행 시 기존 jsonl을 자동 import 후 백업.

---

## 5. [Deferred] 워크플로우 엔진 일반화

### 동기 (잠재적)

`workflow.categories: [plan, implementation, testing, commit]`은 사실상
**선형 4단계 컨벤션**. 다음 패턴이 안 표현됨:

- **스파이크 → 결정 → 구현** (3단계, 결정 노드 분기).
- **마이그레이션 → 검증 → 조건부 적용** (조건부 분기).
- **병렬 후보 N개 → 가장 좋은 것 선택** (multi-armed exploration).
- **a/b/c 중 하나만 성공해도 진행** (or-merge).

### 변경 (만약 한다면)

각 task의 `kind` 필드 도입:

```jsonc
{ "id": "auth-redesign-spike", "kind": "spike",
  "outputs": ["decision.md"] },
{ "id": "auth-redesign-decision", "kind": "decision",
  "dependsOn": ["auth-redesign-spike"],
  "branches": {
    "oauth": ["oauth-impl-...", "oauth-test-..."],
    "saml": ["saml-impl-...", "saml-test-..."]
  } }
```

`PlanGenerator`는 PRD에서 적절한 패턴 선택. `ParallelExecutor`는 sub-DAG
실행과 결정 노드 평가를 지원.

### 잃는 것

- **추상화 비용이 큼**. 단순한 PRD조차 ceremony가 늘어날 위험.
- planner가 잘못된 패턴을 자주 선택하면 사용자 체감 품질 저하.
- 디버깅: "왜 이 분기로 갔는가?"가 새로운 종류의 질문이 됨.

### 왜 deferred인가

YAGNI. 4단계 컨벤션이 **실제로 막히는 사례**가 보고되기 전까지는,
일반화를 추측으로 하는 게 위험. 실제 신호를 본 후 **그 신호의 모양**을
일반화하는 게 안전한 추상화.

### 시작 신호

- 4단계로 자연스럽게 안 풀리는 PRD가 3건 이상 보고됨 (사용자 또는 내부).
- "스파이크 후 결정"이 PRD에 자주 등장.
- 또는: `categories` override 사용 사례가 늘어 패턴화 가능.

---

## 작업 순서 권장

1. **fix2.md 항목 처리** (P1 → P2 → P3) — 운영/보안 우선.
2. **#1 이벤트 버스** — fix2 완료 후. #2~#5의 토대가 됨.
3. **#3 명령 진입점 분리** — 빠른 작업, CI 시그널 보이면 즉시.
4. **#2 컨테이너 격리** — 보안 시그널 보이면 시작.
5. **#4 SQLite, #5 워크플로우 엔진** — 신호 충족 전까지 보류.

---

## 의도적으로 제외한 것

다음은 "더 멋진 아키텍처"처럼 들리지만 Ralph 규모/맥락에서는 **과한
설계**라 판단:

- **마이크로서비스 분리** — Ralph는 CLI. 단일 바이너리가 정답.
- **Daemon/server 모드** — 매번 hydration 비용은 측정해보면 미미함.
- **Plugin 시스템 (DLL 동적 로딩)** — 이벤트 버스(#1)로 80% 충족.
- **gRPC IPC** — IDE 통합이 진짜 요구되기 전까지 불필요.
- **자체 LLM gateway / 캐시 레이어** — Claude SDK가 이미 처리.

---

## 메타데이터

- 작성 일자: 2026-04-30.
- 작성 시점 버전: v1.32 (fix1 머지 완료, fix2 작업 예정).
- 선행 문서: `fix1.md`, `fix2.md`.
- 본 문서는 **로드맵**이며 실행 명세가 아님 — 각 항목 시작 시 별도
  설계 문서/PRD를 작성할 것.
