# Ralph

[English](README.md) | **한국어**

PRD(Product Requirements Document)를 실행 가능한 plan으로 변환해 Claude Code로 실행하는 CLI 태스크 오케스트레이터 — 기본은 git worktree 기반 병렬, 의존성 추적 / exit code 기반 검증 / 충돌 인지 머지 / 누적 비용 게이트 / smoke test / 실시간 진행 모니터링 포함. .NET 8 기반 크로스플랫폼 (Windows, macOS, Linux). 현재 버전: **v1.22**.

**git worktree 기반 병렬 실행을 최초로 구현한 Ralph 변종.** 독립적인 기능들을 격리된 Claude Code 에이전트로 동시에 실행하고 자동으로 다시 머지한다.

---

## 목차

- [Ralph를 쓰는 이유](#ralph를-쓰는-이유)
- [보안 주의](#-보안-주의)
- [다른 Ralph 구현체와의 비교](#다른-ralph-구현체와의-비교)
- [동작 원리](#동작-원리)
- [사례 연구 — Ralph가 자기 자신을 고치다](#사례-연구--ralph가-자기-자신을-고치다)
- [버전](#버전)
- [필수 의존성](#필수-의존성)
- [설치](#설치)
- [빠른 시작](#빠른-시작)
- [명령어](#명령어)
- [실행 옵션](#실행-옵션)
- [환경변수](#환경변수)
- [병렬 실행 흐름](#병렬-실행-흐름)
- [실패 처리 및 재개](#실패-처리-및-재개)
- [충돌 해결 전략](#충돌-해결-전략)
- [검증 게이트(Verification Gate)](#검증-게이트verification-gate)
- [Smoke Test (머지 후)](#smoke-test-머지-후)
- [비용 추적 및 예산 게이트](#비용-추적-및-예산-게이트)
- [Webhook 알림](#webhook-알림)
- [실시간 모니터링](#실시간-모니터링)
- [tasks.json 구조](#tasksjson-구조)
- [Workflow 설정](#workflow-설정)
- [설계 노트 — 왜 `tasks.json`은 mutable + declarative 인가?](#설계-노트--왜-tasksjson은-mutable--declarative-인가)
- [병렬 실행을 위한 PRD 작성법](#병렬-실행을-위한-prd-작성법)
- [로그](#로그)
- [예시](#예시)
- [고려사항(Things to Consider)](#고려사항things-to-consider)
- [트러블슈팅](#트러블슈팅)
- [보안](#보안)
- [기여 및 개발](#기여-및-개발)
- [GitHub Topics](#github-topics)

---

## Ralph를 쓰는 이유

| 기능 | 의미 |
|---|---|
| **기본 병렬** | 독립 기능들이 격리된 git worktree에서 동시에 실행됨 — 수동 오케스트레이션 불필요. |
| **의존성 인지** | `dependsOn` 기반 위상 정렬 DAG가 스케줄링을 결정 — 의존하는 task는 대기, 형제 task는 병렬화. |
| **검증 게이트** | `verification.command`의 exit code가 ground truth. Claude의 self-report는 무시. 기본 self-fix 1회 재시도. |
| **충돌 전략 chain** | `auto-theirs` 시도 → `claude` fallback → `abort` fallback. 프로젝트별 설정 가능. |
| **비용 예산** | `--budget-usd` 하드 상한 + 80% 경고. 호출별 토큰 사용량은 append-only ledger로 기록. |
| **머지 후 smoke test** | 각 배치 머지 후 base 브랜치에서 단일 명령 실행 — auto-merge로 살아남은 semantic 충돌을 잡는다. |
| **재개 안전** | `done: true`는 task별 atomic write — 재실행 시 정확히 중단점부터 이어진다. |
| **Plan 비평** | 정적 `--critique`가 병렬화/검증 누락을 진단. 선택적 `--llm-critique`는 PRD vs plan을 LLM이 한 번 더 검토. |
| **단일 self-contained 바이너리** | 대상 머신에 .NET 런타임 설치 불필요. 스키마와 가격표가 바이너리에 임베드됨. |

## ⚠️ 보안 주의

Ralph는 호스트 머신에서 Claude Code를 `--dangerously-skip-permissions`로 직접 실행한다. 신뢰할 수 없는 PRD나 외부 `tasks.json` 파일은 격리된 환경(별도 사용자 계정, VM 또는 컨테이너)에서 실행해야 한다. 다음과 같은 정보가 노출될 수 있다:

- `~/.ssh`, `~/.aws`, `~/.config` 내 자격 증명
- 환경변수에 저장된 API 키
- 호스트의 모든 파일에 대한 읽기 권한

`PlanValidator`가 declared `outputFiles` / `modifiedFiles`에 포함된 민감 경로를 경고하지만 이는 휴리스틱일 뿐 — sandbox가 아니다.

## 다른 Ralph 구현체와의 비교

| 기능 | snarktank/ralph | PageAI/ralph-loop | starlog/ralph |
|---|---|---|---|
| 병렬 실행 | ❌ | ❌ | ✅ |
| Windows 지원 | ❌ | ❌ | ✅ |
| DAG 의존성 | ❌ | 부분 지원 | ✅ |
| 비용 추적 + 예산 게이트 | ❌ | ❌ | ✅ |
| 검증 게이트 (exit code) | ❌ | ❌ | ✅ |
| 머지 후 smoke test | ❌ | ❌ | ✅ |
| Webhook 알림 | ❌ | ❌ | ✅ |
| 단일 바이너리 | ❌ | ❌ | ✅ |

## 동작 원리

Ralph는 기능 단위로 **4단계 패턴**을 따른다 (`workflow.categories`로 변경 가능):

```
plan → implementation → testing → commit
```

한 기능 안의 4단계는 `dependsOn`으로 항상 직렬화된다. 독립적인 기능들은 git worktree 기반으로 **병렬 실행**되고 다시 base 브랜치로 머지된다.

```
user-auth-plan ─→ user-auth-impl ─→ user-auth-test ─→ user-auth-commit ─┐
                                                                          ├─→ main-plan ─→ ...
payment-plan ─→ payment-impl ─→ payment-test ─→ payment-commit ──────────┘
   (병렬 실행)                                                  (머지 후 순차)
```

## 사례 연구 — Ralph가 자기 자신을 고치다

Ralph로 자기 자신의 소스 코드 정적 분석에서 발견된 버그들을 자동 수정한 사례. 위에서 설명한 파이프라인의 모든 단계를 실제로 사용한다.

- **출발점:** `bugfix.md`에 Ralph 내부 서비스(`LogRotator`, `GitService`, `VerificationRunner`, `RalphLogger`, `WorktreeService`, `ParallelExecutor`, `Program`, `PlanGenerator`)에서 발견한 **독립 버그 9개**와 **선택적 cosmetic 리팩토링 1개**를 정리. 각 항목은 1~2개 파일로 한정되며 `modifiedFiles`가 명시되어 있다.
- **분해:** `ralph --plan bugfix.md`가 PRD를 작은 `*-impl` / `*-commit` task 쌍으로 변환. 서로 다른 파일을 수정하는 7개 버그는 **하나의 완전 병렬 layer**를 이루고, `WorktreeService.cs`를 함께 건드리는 두 항목(Feature 5와 선택적 Feature 10)만 `dependsOn`으로 직렬화된다.
- **실행:** `ralph --run`이 최대 **5개 worktree를 동시에** dispatch (`workflow.parallel.maxConcurrent: 5`). 각 task는 `.ralph-worktrees/` 아래 `ralph/{taskId}` 브랜치에서 격리 실행되고 Claude Code 스트림이 task별 로그로 기록된다.
- **머지:** 머지 직전 각 worktree 브랜치를 최신 base로 rebase한 뒤, `conflictStrategies: ["auto-theirs", "claude"]` 체인으로 사소한 충돌은 `-X theirs`로 자동 해결하고 나머지만 Claude에게 escalate.
- **검증:** task마다 `verification.command`(`dotnet build` 또는 `dotnet test --filter ...`)의 exit code를 ground truth로 사용 — Claude의 self-report는 무시. 실패하면 1회 self-fix 재시도 후에도 안 되면 머지에서 제외된다.
- **결과:** PRD가 겨냥하는 바로 그 오케스트레이터가 자기 자신을 수정한다 — plan 생성부터 병렬 배치 스케줄링, 머지, 검증까지 사용자가 개입하는 지점은 처음의 `ralph --run` 한 번뿐.

전체 PRD: [bugfix.md](bugfix.md)

## 버전

| 버전 | 구현 | 플랫폼 | 주요 기능 |
|---|---|---|---|
| v0.1 | `ralph.sh` / `ralph.ps1` (Bash / PowerShell, 현재 [`legacy/`](legacy/) 디렉토리로 이동) | macOS, Linux, Windows | 순차 실행 |
| v0.6 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | 병렬 실행, worktree, live log |
| v0.7 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | `--graph` 태스크 의존성 그래프 |
| v1.0 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | 비용 추적, 플랜 검증, prompt builder, webhook 알림, 로그 로테이션 |
| v1.1 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | 검증 게이트, 충돌 전략 chain, 머지 후 smoke test, `--task-timeout`, `--budget-usd`, `--strict-files`, `--shared-worktrees`, `--critique` / `--llm-critique`, 머지 직전 worktree rebase |
| v1.2 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | `IAgentRunner` 추상화 + 실시간 비용 표시, longest-prefix 가격표 매칭, `MockAgentRunner` 테스트 헬퍼, smoke test 자동 추론 + opt-out, `--llm-critique`, `--shared-worktrees`, 충돌 비용 별도 요약, 패키지 매니저 매니페스트(Homebrew tap, Scoop), ParallelExecutor 리팩토링 + 통합 테스트 |
| v1.21 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | Plan 검증 자동 정정 루프(invalid plan + errors를 Claude에게 재전송, 최대 2회), `SmokeTestPlanner` 분리 및 다중 marker 인식, Python marker 지원, Windows 인터프리터 해석을 위한 `HostPlatform`, 릴리스 자동화 강화 |
| v1.22 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | 릴리스 스크립트가 claude CLI로 버전 표기 자동 동기화, Windows에서 한국어 커밋 요약이 stdout 쓰기 실패로 릴리스를 죽이지 않도록 UTF-8 콘솔 인코딩 고정 |

## 필수 의존성

| 도구 | 설명 |
|---|---|
| [Claude Code](https://claude.ai/code) | Claude Code CLI |
| [git](https://git-scm.com/) | 버전 관리 (worktree 기반 병렬 실행에 필수, `--shared-worktrees` 사용 시 git 2.10+) |

## 설치

### 방법 1: 설치 스크립트 (컴파일된 바이너리를 로컬에 설치)

.NET 8 SDK가 필요하다. 스크립트는 플랫폼에 맞는 self-contained Ralph 바이너리를 publish한 뒤 PATH 상의 디렉토리(기본 `~/bin`)로 복사한다. 결과물은 방법 2와 동일한 단일 바이너리이며, 다운로드 대신 직접 빌드한다는 차이만 있다.

**macOS / Linux:**

```bash
git clone https://github.com/starlog/ralph.git
cd ralph
./install.sh
```

**Windows (PowerShell):**

```powershell
git clone https://github.com/starlog/ralph.git
cd ralph
.\install.ps1
```

### 방법 2: 사전 빌드된 바이너리 다운로드

[GitHub Releases](https://github.com/starlog/ralph/releases) 페이지에서 플랫폼에 맞는 바이너리를 다운로드한다. .NET SDK 설치가 필요 없다.

| 플랫폼 | 파일 |
|---|---|
| Windows (x64) | `ralph-vX.X.X-win-x64.zip` |
| macOS (Intel) | `ralph-vX.X.X-osx-x64.tar.gz` |
| macOS (Apple Silicon) | `ralph-vX.X.X-osx-arm64.tar.gz` |
| Linux (x64) | `ralph-vX.X.X-linux-x64.tar.gz` |

```bash
# 예: Linux
curl -LO https://github.com/starlog/ralph/releases/latest/download/ralph-v1.22-linux-x64.tar.gz
tar -xzf ralph-v1.22-linux-x64.tar.gz
sudo mv ralph /usr/local/bin/
```

바이너리는 self-contained이므로 .NET 런타임이 필요 없다.

### 방법 3: 패키지 매니저

```bash
# macOS / Linux — Homebrew tap
brew tap starlog/ralph https://github.com/starlog/ralph
brew install ralph

# Windows — Scoop (커스텀 manifest)
scoop install https://raw.githubusercontent.com/starlog/ralph/main/scoop/ralph.json
```

매니페스트는 [`Formula/ralph.rb`](Formula/ralph.rb)와 [`scoop/ralph.json`](scoop/ralph.json)에 있으며 최신 GitHub 릴리스를 추적한다.

## 빠른 시작

```bash
# 1. PRD에서 task plan 생성
ralph --plan docs/PRD.md

# 2. (선택) 생성된 plan 비평
ralph --critique                            # 정적 점검: 병렬화 / 검증 / 의존성 이상
ralph --plan docs/PRD.md --llm-critique    # LLM 기반 추가 검토 (비용 발생)

# 3. 생성된 plan 검증 (cycle, 의존성, 파일 중복, 민감 경로)
ralph --validate

# 4. 생성된 task 확인
ralph --list
ralph --graph

# 5. dry-run으로 미리보기 (실제 변경 없음)
ralph --dry-run

# 6. 전체 파이프라인 실행
ralph --run
```

## 명령어

| 명령 | 설명 |
|---|---|
| `--plan <file>` | PRD를 분석해 `tasks.json` 생성 (atomic write) |
| `--plan-prompt <file>` | 실제 실행 없이 plan prompt 전체 출력 |
| `--validate` | `tasks.json` 검증 (cycle, dangling deps, 중복 ID, 파일 중복, 민감 경로) |
| `--critique` | `tasks.json` 정적 비평 (병렬화 / 검증 누락 / 의존성 이상) |
| `--run [file]` | 모든 pending task 실행 (기본 병렬). 기본 `tasks.json` |
| `--dry-run [file]` | 실행 시뮬레이션. 종료 시 `tasks.json` 복원 |
| `--task <id>` | ID로 단일 태스크 실행 (의존성 무시는 `--force`) |
| `--interactive` | 인터랙티브 모드 — 매 태스크 확인 |
| `--list`, `-l` | pending task 목록 (병렬 가능 여부 표시) |
| `--graph`, `-g` | ASCII 의존성 그래프 |
| `--prompts`, `-p` | 모든 task의 Claude prompt 출력 |
| `--show-prompt <id>` | 단일 task에 보낼 prompt 전체 출력 |
| `--status`, `-s` | 진행 대시보드 (병렬 배치 정보 포함) |
| `--cost` | 누적 토큰 사용량 + USD 추정 |
| `--reset`, `-r` | 모든 task를 pending으로 리셋 |
| `--logs` | 로그 파일 목록 (세션 + 태스크별) |
| `--logs <task-id>` | 특정 태스크 로그 출력 |
| `--logs --live <task-id>` | 태스크 로그 라이브 tail (`tail -f`처럼) |
| `--logs --cleanup` | 보존 기간 초과 로그 삭제 |
| `--worktree-cleanup` | 남은 worktree 정리 |
| `--help`, `-h` | 도움말 표시 |

### 실행 옵션

| 옵션 | 설명 |
|---|---|
| `-f`, `--file <path>` | 커스텀 tasks 파일 (대부분 명령에서 동작) |
| `--sequential` | 병렬 실행 비활성 — 한 번에 하나씩 |
| `--max-parallel N` | 동시 실행 task 수 상한 |
| `--force` | 의존성/검증 무시 (`--task` / `--run`과 함께) |
| `--strict-files` | 머지 후 declared vs actual `modifiedFiles` 검증; undeclared 발견 시 중단 |
| `--shared-worktrees` | `git worktree add --shared`로 `.git` objects 공유 (디스크/IO 절약, 미지원 시 자동 fallback) |
| `--no-smoke-test` | 머지 후 smoke test 건너뜀 (그렇지 않으면 자동 추론 또는 `workflow.smokeTest` 사용) |
| `--budget-usd <amt>` | 누적 비용이 `<amt>` USD에 도달하면 새 task dispatch 중단 |
| `--task-timeout <dur>` | per-Claude 호출 timeout (예: `30m`, `1h`, `90s`, `1800`) — 멈춘 호출 강제 종료 |
| `--llm-critique` | `--plan` 직후 PRD + plan에 대한 LLM 기반 비평 1회 추가 (기본 off, 추가 비용) |
| `--model <name>` | 모델: `sonnet` 또는 `opus` (기본: `opus`) |
| `--debug` | Claude stream 이벤트 출력 (진단용) |

### 커스텀 tasks.json

기본 파일이 아닌 다른 파일을 가리키는 두 가지 방법:

```bash
ralph --run my-project-tasks.json     # positional (run/dry-run/list/graph 등)
ralph -f my-project-tasks.json --run  # 글로벌 -f / --file 플래그
```

### 인터랙티브 모드

`--interactive`는 각 태스크 전에 다음 선택지를 제공한다:

- `Yes - Execute` — 태스크 실행
- `Preview prompt` — prompt만 보여주고 실행하지 않음
- `Skip` — 이 태스크 건너뜀
- `Quit` — 종료

## 환경변수

| 변수 | 기본값 | 설명 |
|---|---|---|
| `MAX_RETRIES` | 2 | Claude Code 호출 재시도 횟수 |
| `RETRY_DELAY` | 5 | 재시도 간 대기(초) |
| `RALPH_MAX_PARALLEL` | 0 (tasks.json 사용) | 동시 실행 task 수 오버라이드 |
| `RALPH_PARALLEL` | true | `false`면 병렬 비활성 |
| `RALPH_STRICT_FILES` | false | `true`면 `--strict-files` 기본 활성화 |
| `RALPH_SHARED_WORKTREES` | false | `true`면 `--shared-worktrees` 기본 활성화 |
| `RALPH_NO_SMOKE_TEST` | false | `true`/`1`이면 머지 후 smoke test 비활성 |
| `RALPH_BUDGET_USD` | unset | 누적 비용 상한 — CLI `--budget-usd` 우선 |
| `RALPH_TASK_TIMEOUT_SEC` | unset | per-Claude 호출 timeout(초) — CLI `--task-timeout` 우선 |
| `RALPH_WEBHOOK_URL` | unset | 세션 완료 webhook 기본값 |
| `RALPH_LOG_RETENTION_DAYS` | 30 | N일 초과 로그 자동 삭제 |

공통 설정의 우선순위: **CLI 플래그 > 환경변수 > `tasks.json`의 `workflow` 설정 > 내장 기본값.**

```bash
# Linux/macOS
MAX_RETRIES=3 ralph --run
RALPH_MAX_PARALLEL=4 ralph --run
RALPH_BUDGET_USD=10.00 ralph --run
RALPH_TASK_TIMEOUT_SEC=1800 ralph --run

# Windows (PowerShell)
$env:MAX_RETRIES=3; ralph --run
$env:RALPH_PARALLEL="false"; ralph --run    # 강제 순차 모드
```

## 병렬 실행 흐름

Ralph는 git worktree로 독립 task를 동시 실행한다. 의존성 그래프가 스케줄링을 결정 — `dependsOn`이 비어 있는 task는 모두 병렬 후보다.

```
ralph --run
```

1. 최소 1개 commit 존재 확인 (worktree 생성 전제, 없으면 자동 초기 commit 생성).
2. stale worktree 감지 및 정리.
3. 위상 정렬로 ready task를 병렬 배치로 그룹화.
4. task당 git worktree 생성 (`ralph/{taskId}` 브랜치, `.ralph-worktrees/` 아래). `--shared-worktrees` 시 공유.
5. 각 worktree에서 Claude Code 동시 실행 (라이브 진행 대시보드).
6. `verification.command` 정의 시 실행. 실패 시 `workflow.verifyRetries`까지 self-fix 재시도.
7. 머지 직전 worktree 브랜치를 최신 base로 rebase (advance).
8. (선택) declared `modifiedFiles`만 머지에 포함됐는지 검증 (`--strict-files`는 undeclared 발견 시 중단).
9. 완료된 브랜치를 base 브랜치로 순차 머지.
10. 머지 충돌은 `conflictStrategies` 체인으로 해결.
11. `done: true` thread-safe 마킹, `tasks.json` atomic save, 변경 commit.
12. base 브랜치에서 머지 후 smoke test 실행 (자동 추론 또는 `workflow.smokeTest`).
13. 다음 배치(unblock된 task)로 진행.
14. 마지막에 task가 1개 남으면 in-place 실행으로 fallback.

## 실패 처리 및 재개

병렬 배치가 부분 실패한 경우 동작:

| 상황 | 동작 |
|---|---|
| 같은 배치 내 한 task에서 Claude 실패 | 다른 task들은 **정상 진행 + 머지**. 실패 task는 worktree 정리, `done` 플래그 false 유지. |
| `verification.command` 실패 | `workflow.verifyRetries` (기본 1)까지 stdout/stderr를 context로 self-fix 재시도. 그래도 실패하면 task 실패 처리 + **머지에서 제외**. |
| Pre-merge scope 위반 (`--strict-files`) | 머지 전에 worktree 빠르게 실패 — cleanup 비용 절약. 같은 배치의 다른 task는 영향 없음. |
| 충돌 전략 chain으로도 해결 불가능한 머지 충돌 | 남은 미머지 worktree들은 정리. 이미 머지된 task는 **그대로 유지** (롤백 없음). |
| 머지 후 `workflow.smokeTest` 실패 | non-zero exit로 ralph 종료. 머지는 revert되지 않으며 실패 내용은 로그 + 표시. |

**중단 후 재개:**
- `done: true`는 task 단위로 atomic write — `ralph --run`을 다시 실행하면 정확히 중단점부터 (오직 `done: false` task만 dispatch).
- `--run` 시작 시 worktree에 uncommitted 변경 또는 base 대비 commit이 남아있으면 **조용히 삭제하지 않는다.** worktree 경로를 출력하고 사용자가 직접 머지/정리하거나 `ralph --worktree-cleanup`으로 강제 제거하도록 안내.
- 작업이 사라지지 않은 깔끔한 stale worktree는 자동 제거.

**이미 머지된 task는 자동 롤백되지 않는다.** Ralph 설계상 머지가 commit point — 되돌리려면 사람이 `git revert` / `git reset`을 실행해야 한다. 머지가 영구화되기 전에 문제를 잡으려면 `--strict-files`와 `workflow.smokeTest`를 활용.

## 충돌 해결 전략

`tasks.json`의 `workflow.parallel.conflictStrategies` (체인) 또는 legacy `workflow.parallel.conflictStrategy` (단일)로 설정. 체인은 **순서대로 시도하는 fallback list** — 첫 항목은 초기 머지 `-X` 플래그(`auto-*`용)를 결정하고, 나머지 항목은 직전 단계 실패 시 차례로 시도된다.

| 전략 | 동작 |
|---|---|
| `claude` | Claude Code가 충돌 마커를 분석해 양쪽을 통합 (체인 마지막에 두는 것을 권장) |
| `abort` | 머지를 중단하고 task를 순차 모드로 재실행 |
| `auto-theirs` | git `-X theirs` — worktree 브랜치 변경을 우선 |
| `auto-ours` | git `-X ours` — base 브랜치 변경을 우선 |

예시 — 사소한 충돌은 `-X theirs`로 자동, `-X theirs`로 못 푸는 경우(add/add, rename/delete)만 Claude로 escalate:

```json
"conflictStrategies": ["auto-theirs", "claude"]
```

Claude 해결 후에는 `git diff --check --cached`로 staged 영역에 충돌 마커가 남아있지 않은지 한 번 더 확인한다.

## 검증 게이트(Verification Gate)

각 task는 `verification.command`를 선언할 수 있고, exit code가 ground truth가 된다 — Claude의 self-report는 무시. non-zero exit 시 stdout/stderr를 context로 Claude에게 self-fix 재시도를 요청 (횟수: `workflow.verifyRetries`, 기본 1).

```json
{
  "id": "math-impl",
  "verification": { "command": "go test ./...", "timeoutSec": 120 }
}
```

명령은 task의 작업 디렉토리(병렬은 worktree, 순차는 repo 루트)에서 POSIX `/bin/sh -c` 또는 Windows `cmd /c`로 실행된다. 모든 시도는 `.ralph-logs/validation.jsonl`에 기록.

자주 쓰는 명령: `pytest tests/`, `go test ./...`, `tsc --noEmit`, `dotnet test`, `npm test --silent`, `cargo test --quiet`.

## Smoke Test (머지 후)

매 병렬 배치 머지 완료 후, Ralph는 base 브랜치에서 **단 한 번** smoke test를 실행해 auto-merge 또는 Claude 해결로도 살아남은 semantic 충돌을 잡는다.

우선순위:

1. `--no-smoke-test` / `RALPH_NO_SMOKE_TEST=true` — 완전 비활성.
2. `tasks.json`의 명시적 `workflow.smokeTest`.
3. repo 루트 marker로 자동 추론:
   - `*.csproj` / `*.sln` → `dotnet build -nologo`
   - `package.json` → `npm test --silent`
   - `Cargo.toml` → `cargo build --quiet`
   - `go.mod` → `go build ./...`
4. marker 없음 → smoke test 자동 스킵.

실패 시 ralph는 non-zero exit로 종료. 머지는 revert되지 않는다.

```json
"workflow": {
  "smokeTest": { "command": "dotnet build", "timeoutSec": 180 }
}
```

## 비용 추적 및 예산 게이트

Claude `stream-json`의 `result` 이벤트에서 호출별 사용량을 `.ralph-logs/cost.jsonl`에 기록 (로그 로테이션에서 보존). `--budget-usd <amt>` (또는 `RALPH_BUDGET_USD`)는 누적 비용이 상한에 도달하면 새 dispatch를 차단하고, 80%에 1회 경고를 띄운다.

```bash
ralph --cost                            # 누적 토큰 + USD 출력
ralph --run --budget-usd 5.00           # $5 도달 시 새 dispatch 중단
```

가격은 임베드된 `pricing.json`에서 로드되며 `~/.ralph/pricing.json`으로 오버라이드 가능.

예산 게이트는 **이미 실행 중인 task를 중단시키지 않는다** — 새 dispatch만 막으므로 이미 시작된 task의 비용만큼 초과 가능.

## Webhook 알림

세션 종료 시 webhook 1회 발송. 우선순위:

1. `tasks.json`의 `workflow.notifications.onComplete` / `onFailure`
2. `RALPH_WEBHOOK_URL` 환경변수 (글로벌 fallback)

`format`은 hostname으로 자동 감지(`hooks.slack.com` → Slack, `discord(app)?.com` → Discord, 그 외 → `generic`)되고 `workflow.notifications.format`으로 강제 가능.

Slack은 `{text, blocks}`, Discord는 `{content, embeds}`, `generic`은 Ralph의 구조화 JSON.

## 실시간 모니터링

병렬 실행 중 다른 터미널에서 task 로그 라이브 tail:

```bash
# 터미널 1: 실행
ralph --run

# 터미널 2: 한 task 라이브 tail
ralph --logs --live add-impl

# 터미널 3: 다른 task 라이브 tail
ralph --logs --live subtract-impl
```

메인 `--run` 콘솔에는 Spectre.Console 라이브 테이블로 worktree별 상태/경과 시간/현재 Claude phase가 표시된다.

## tasks.json 구조

`tasks.json`은 `ralph --plan`이 생성하거나 직접 작성한다. 전체 스키마는 `ralph-schema.json`에 정의되어 있으며 바이너리에 임베드되어 있다.

### 최소 예시

```json
{
  "projectName": "my-project",
  "version": "1.0.0",
  "tasks": [
    {
      "id": "setup-plan",
      "title": "Project setup plan",
      "done": false,
      "phase": "phase1-setup",
      "category": "plan",
      "prompt": "Analyze the project structure and draft a setup plan...",
      "outputFiles": ["docs/setup-plan.md"]
    }
  ]
}
```

### Task 객체

| 필드 | 필수 | 타입 | 설명 |
|---|---|---|---|
| `id` | **yes** | string | kebab-case 고유 ID (`^[a-zA-Z0-9_-]+$`) |
| `title` | **yes** | string | task 제목 (≤ 200자) |
| `done` | **yes** | boolean | 완료 플래그 — 실행 후 자동으로 `true` |
| `description` | | string | 상세 설명 |
| `phase` | | string | 프로젝트 단계 (`"phase1"`, `"phase2"` 등) |
| `category` | | string | 카테고리 (`"plan"`, `"implementation"`, `"testing"`, `"commit"` 또는 `workflow.categories`에 명시된 값) |
| `prompt` | | string | Claude Code에 전달되는 prompt; 비어있으면 Claude 호출 생략 |
| `outputFiles` | | string[] | 생성/수정 예상 파일 경로 |
| `modifiedFiles` | | string[] | 이 task가 수정할 파일 — 병렬 충돌 감지 + `--strict-files`에 사용 |
| `dependsOn` | | string[] | 선행 task ID 목록; 비어있으면 병렬 후보 |
| `subtasks` | | array | 선택적 subtask |
| `verification` | | object | `{ command, timeoutSec? }` — exit code 기반 검증 (위 [검증 게이트](#검증-게이트verification-gate) 참고) |

## Workflow 설정

```json
{
  "workflow": {
    "onTaskComplete": {
      "commitChanges": true,
      "commitMessageTemplate": "[Task #{taskId}] {taskTitle}"
    },
    "parallel": {
      "enabled": true,
      "maxConcurrent": 5,
      "conflictStrategies": ["auto-theirs", "claude"],
      "sharedWorktreeObjects": false
    },
    "notifications": {
      "onComplete": "https://hooks.slack.com/services/XXX",
      "format": "slack"
    },
    "logRetentionDays": 30,
    "budgetUsd": 10.00,
    "taskTimeoutSec": 1800,
    "maxRetries": 2,
    "retryDelay": 5,
    "verifyRetries": 1,
    "smokeTest": { "command": "dotnet build", "timeoutSec": 180 },
    "categories": ["plan", "implementation", "testing", "commit"]
  }
}
```

| 설정 | 기본값 | 설명 |
|---|---|---|
| `parallel.enabled` | true | 병렬 실행 활성화 |
| `parallel.maxConcurrent` | 5 | 최대 동시 task 수 (상한 10) |
| `parallel.conflictStrategy` | `"claude"` | legacy 단일 전략 (`conflictStrategies` 미설정 시만 사용) |
| `parallel.conflictStrategies` | (unset) | 순서 있는 fallback chain — `conflictStrategy`보다 우선 |
| `parallel.sharedWorktreeObjects` | false | `git worktree add --shared` 사용 (git 2.10+ 필요) |
| `notifications.onComplete` / `onFailure` | (unset) | 세션 webhook URL |
| `notifications.format` | auto | `generic` / `slack` / `discord` |
| `logRetentionDays` | 30 | `.ralph-logs/`의 오래된 로그 자동 삭제 (`cost.jsonl`, `validation.jsonl`은 보존) |
| `budgetUsd` | (unset) | 누적 비용 상한 — CLI/env가 우선 |
| `taskTimeoutSec` | (unset) | per-Claude 호출 timeout — CLI/env가 우선 |
| `maxRetries` | 2 | Claude 호출당 재시도 (env `MAX_RETRIES` 우선) |
| `retryDelay` | 5 | 재시도 간 대기(초) (env `RETRY_DELAY` 우선) |
| `verifyRetries` | 1 | `verification.command` 실패 시 self-fix 재시도 (0이면 비활성) |
| `smokeTest` | (unset → 자동 추론) | 머지 배치 후 base 브랜치에서 실행할 단일 명령 |
| `categories` | `["plan","implementation","testing","commit"]` | `--plan`에서 사용할 기능별 stage 목록 오버라이드 |

## 설계 노트 — 왜 `tasks.json`은 mutable + declarative 인가?

`tasks.json`은 두 역할을 동시에 한다:

- **Declarative** — 의도의 manifest다: 어떤 task가 있는지, 각 task가 무엇을 해야 하는지, 어떤 파일을 건드리는지, 무엇이 검증하는지, 서로 어떻게 의존하는지. `--plan`이 작성하고 사람이 손댈 수 있다.
- **Mutable** — 실행 도중 Ralph 자신이 다시 쓴다: task별 `done: true` 플립, 배치마다 자동 commit, `--reset`이 다시 덮어쓴다.

이 두 역할을 **하나의 파일**에 합친 것은 의도된 trade-off다.

### 한 파일로 합친 장점

- **재개 안전이 자연스럽게 보장된다.** 상태가 spec과 같은 곳에 살기 때문에 `ralph --run`을 다시 실행하면 정확히 멈춘 자리에서 이어진다 (오직 `done: false` task만 dispatch). 동기화가 필요한 sidecar DB나 별도 state 디렉토리가 없다.
- **단일 audit 가능 산출물.** `git log tasks.json`이 곧 실행 이력 — 무엇을 계획했고 무엇을 완료했고 언제 했는지가 한 줄로 보인다. PR에서 plan과 progress를 함께 리뷰할 수 있다.
- **오프라인/이식성.** 조정 서비스가 필요 없다. 파일(과 repo)을 어디로든 복사하면 전체 파이프라인이 그대로 기술된다.
- **모델이 단순하다.** "spec.json (immutable) + state.json (mutable)" 분리, 둘을 일치시키는 동기화 로직, drift 가능성이 모두 사라진다.

### 그 대가로 잃는 것

- **실행 중 편집은 위험하다.** writer가 두 명(사용자 + Ralph)이라 race가 가능하다. Ralph는 atomic tmp+rename, `MarkTaskDone`을 둘러싼 in-process file lock, 머지 사이의 reload로 완화하지만 — `--run` 도중의 손편집은 피해야 한다.
- **하나의 파일 안에 관심사가 섞인다.** "무엇을 해야 하는가"와 "이미 무엇이 완료됐는가"가 같은 스키마에 공존한다. 리뷰어는 의도와 진행을 분리해서 보아야 한다.
- **`tasks.json` 자체의 머지 충돌.** 각 worktree가 자기 task의 `done` 플립으로 `tasks.json`을 건드릴 수 있다. Ralph는 머지 직전에 worktree의 `tasks.json`을 base 브랜치 기준으로 정규화해 완화하지만 충돌 표면이 0은 아니다.
- **provenance가 흐려진다.** `--plan` (LLM)이 쓰고, 사람이 손대고, Ralph가 변형한다. 중간 commit이 없으면 "어떤 줄을 누가 썼는가"를 추적하기 어렵다.
- **`--reset`은 파괴적이다.** 사람이 직접 손댄 같은 파일을 다시 덮어쓴다. version control이 안전망 역할을 한다.

### Ralph가 적용한 완화책

- **Atomic write** (`tmp + rename`) — crash가 와도 partial 파일이 절대 안 남는다.
- **In-process lock** — 병렬 배치에서 task done 마킹 시 단일 직렬화.
- **Pre-merge 정규화** — 각 worktree의 `tasks.json`을 머지 전에 base 기준으로 reset, 결과적으로 Ralph의 `done` 플립만 머지 commit에 포함된다.
- **`--dry-run` try/finally** — preview 실행은 항상 원본 `tasks.json`을 복원한다.
- **배치 단위 자동 commit** — `done: true` 플립이 즉시 commit되므로 crash가 와도 진행 기록이 사라지지 않는다.

### 이 설계가 맞지 않는 경우

엄격한 spec/state 분리가 필요하다면(예: plan에 서명해서 freeze하고 state는 다른 곳에 보관) `tasks.json`을 spec으로 version control에 두고 `--dry-run`을 read-only 점검으로만 사용하는 방식이 가능하다. Ralph의 mutation은 `done` 플래그 + 일부 subtask 플립에 한정되며, plan 생성 후에 prompt나 의존성을 다시 쓰지는 않는다.

## 병렬 실행을 위한 PRD 작성법

`ralph --plan`이 병렬 친화적인 `tasks.json`을 만들도록 하려면 PRD에서 **독립 기능을 명확히 분리**한다.

**독립 기능** = 서로 다른 파일을 건드리고 다른 기능의 코드를 참조하지 않는다.

plan generator가 의존성을 결정하는 규칙:
- 한 기능 내부의 4단계(plan → impl → test → commit)는 항상 직렬
- 출력이 겹치지 않는 두 기능 → 병렬 후보
- 다른 기능의 출력에 의존하는 기능 → `dependsOn`으로 연결

### 좋은 PRD 구조

기능을 독립 모듈로 쪼개고 공유되는 토대는 별도 phase로 분리:

```markdown
# PRD: 계산기 앱

## Phase 1 — 연산 모듈 (독립, 병렬 실행)

### 덧셈 모듈
- `add.py`에 add(a, b) 구현
- `tests/test_add.py`에 테스트 추가

### 뺄셈 모듈
- `subtract.py`에 subtract(a, b) 구현
- `tests/test_subtract.py`에 테스트 추가

## Phase 2 — 메인 진입점 (Phase 1 이후)

### CLI main
- `main.py`에서 연산 모듈을 import해 CLI 노출

## Phase 3 — 통합 테스트 (Phase 2 이후)
```

병렬화를 유도하는 팁:

| 전술 | 효과 |
|---|---|
| **기능별로 정확한 파일 목록 명시** | plan generator가 정확한 `modifiedFiles`를 생성 |
| **Phase 분리** | 같은 phase에 독립 기능, 다음 phase에 의존 기능 배치 |
| **힌트 키워드** | PRD에 "독립적", "병렬 실행 가능" 같은 표현 사용 |
| **공유 코드 최소화** | 공통 유틸을 첫 phase에 두고 나머지는 의존 |
| **의존성을 명시적으로 기술** | "X 모듈은 Y에 의존" → 정확한 `dependsOn` |

`ralph --plan` 후 `ralph --critique`로 결과 `tasks.json`에 대한 정적 리포트(병렬화 누락, verification 누락, 의존성 이상)를 받을 수 있다. `--plan` 시 `--llm-critique`를 추가하면 PRD vs plan에 대한 LLM 기반 검토도 수행한다.

## 로그

실행 로그는 `.ralph-logs/`에 기록된다:

```
.ralph-logs/
├── ralph-20260219-165209.log   # 세션 로그
├── add-plan.log                # task별 로그 (병렬 실행)
├── subtract-plan.log
├── multiply-plan.log
├── cost.jsonl                  # 누적 토큰 사용량 / 비용 ledger (보존)
└── validation.jsonl            # 검증 명령 ledger (보존)
```

```bash
ralph --logs                    # 로그 파일 목록
ralph --logs add-impl           # 특정 task 로그 출력
ralph --logs --live add-impl    # 라이브 tail
ralph --logs --cleanup          # 보존 기간 초과 로그 삭제 (기본 30d)
```

`cost.jsonl`과 `validation.jsonl`은 로그 로테이션에서도 보존되어 이력이 사라지지 않는다.

## 예시

`samples/PRD.md` — 작은 Python 계산기를 만드는 병렬 친화적 PRD:

- **Phase 1** — 연산 모듈 4개 (`add.py`, `subtract.py`, `multiply.py`, `divide.py`)가 병렬로 실행
- **Phase 2** — `main.py`가 4개를 모두 import → Phase 1 이후 순차 실행
- **Phase 3** — 통합 테스트, Phase 2 이후

```bash
mkdir my-calculator && cd my-calculator
cp /path/to/ralph/samples/PRD.md .

ralph --plan PRD.md       # 24 task (병렬 시작점 4개)
ralph --validate          # 생성된 plan 점검
ralph --status            # 병렬 배치 구조 확인
ralph --run               # Phase 1은 4-wide 병렬, Phase 2-3은 순차
```

## 고려사항(Things to Consider)

실제 repo에서 Ralph를 돌리기 전에 알아두어야 할 제약, 함정, 설계 선택의 비완결적 목록.

### 저장소 상태

- Ralph는 **commit이 최소 1개 있는 git 저장소**가 필요하다. 없으면 자동으로 초기 commit을 만든다.
- worktree는 `.ralph-worktrees/{taskId}`, 해당 브랜치는 `ralph/{taskId}`로 생성된다. 아직 안 되어 있으면 `.gitignore`에 둘 다 추가하자.
- 이전 실행에서 남은 `ralph/*` 브랜치는 감지되어 (깨끗하면) 자동 제거된다. **`ralph/*` 브랜치에 uncommitted 변경이나 미머지 commit이 있으면 Ralph는 중단하고 사용자에게 처리를 맡긴다** — 작업을 조용히 파괴하지 않는다.

### 동시성

- 기본 `maxConcurrent`는 **5**이고 상한은 **10**이다. 더 큰 값을 줘도 잘리는데, 대부분의 repo는 CPU보다 디스크/IO나 Claude API rate limit에 먼저 부딪히기 때문.
- `--max-parallel N`은 `tasks.json` 포함 모든 설정을 오버라이드한다.
- `--shared-worktrees`는 worktree가 많을 때 디스크와 `.git` IO를 절약하지만 git 2.10+가 필요하다 — 미지원이면 자동 fallback.

### 머지

- 머지는 배치 내 모든 task가 끝난 **후 순차** 진행. 첫 머지가 base 브랜치를 advance시켜 뒤따르는 worktree들의 base가 바뀐다 — Ralph는 각 worktree를 머지 직전에 새 base로 rebase해 후행 충돌을 줄인다.
- 충돌 전략 chain은 순서대로 실행된다. 첫 항목은 초기 `git merge -X` 플래그(`auto-*`용), 이후 항목은 직전 단계 실패 시에만 시도된다. 해결 불가 케이스가 조용히 실패하지 않도록 chain은 항상 `claude` 또는 `abort`로 끝내는 것이 안전.
- `--strict-files`는 **undeclared write**만 잡는다 — declared 파일이 모두 수정됐음을 보장하진 않는다. 그건 verification gate나 smoke test의 몫.

### 비용

- 예산 게이트(`--budget-usd`)는 **이미 실행 중인 task를 죽이지 않는다** — 새 dispatch만 막으므로 이미 실행 중이던 task의 비용만큼 초과 가능.
- 비용은 Claude가 보고하는 토큰 사용량과 `pricing.json`으로 계산된다. pricing에 모델이 없으면 USD 0으로 기록된다.
- `--llm-critique`는 `--plan`마다 **추가 Claude 호출 1회**가 발생하며 기본은 off.

### 검증 & smoke test

- 검증 명령은 task의 작업 디렉토리에서 실행된다. 그 디렉토리에서 도구(pytest, dotnet 등)가 사용 가능해야 한다 — worktree는 repo 상태는 상속하지만 셸 alias는 상속하지 않는다.
- `verifyRetries`는 기본 **1**. flaky 테스트라면 토큰 비용을 감수하고 늘리고, 빠르게 실패하길 원하면 `0`.
- 머지 후 smoke test는 **opt-out**, opt-in이 아니다. 원치 않으면 `--no-smoke-test` (또는 `RALPH_NO_SMOKE_TEST=true`); 그렇지 않으면 일반 빌드 시스템에 대해 자동 추론된다. 명시적 `workflow.smokeTest`가 항상 우선.

### 민감 경로

- `PlanValidator`는 declared 경로가 `.env`, `*.pem`, `*.key`, `credentials.json`, `id_rsa`, `id_ed25519` 등에 매칭되면 경고하고, auto-commit 단계에서 제외한다.
- 이는 **best-effort 휴리스틱이지 sandbox가 아니다.** 신뢰할 수 없는 PRD는 호스트 사용자 권한으로 어떤 파일이든 읽고 쓰도록 Claude에게 지시할 수 있다. 신뢰할 수 없는 plan은 실제 자격증명이 없는 VM/컨테이너에서 실행하라.

### 결정성(Determinism)

- Plan 생성은 비결정적(LLM 출력)이다. 같은 PRD에 대해 `ralph --plan`을 두 번 돌리면 약간 다른 `tasks.json`이 나올 수 있다. 재현성이 필요하면 생성된 plan을 version control에 고정하자.
- `--run` 안의 Claude 실행 역시 비결정적이다. 파이프라인의 신뢰도는 **검증 게이트**가 만든다 — 약한 검증 = 약한 실행.

### 플랫폼 노트

- self-contained 바이너리는 `win-x64`, `osx-x64`, `osx-arm64`, `linux-x64`로 제공된다. 그 외 플랫폼은 `install.sh` / `install.ps1`로 소스에서 빌드.
- 검증 명령은 POSIX는 `/bin/sh -c`, Windows는 `cmd /c`로 실행된다. 크로스플랫폼 셸 기능(예: POSIX 전용 redirection)은 균일하게 동작하지 않는다.
- ANSI/Spectre.Console 출력은 UTF-8 콘솔을 가정 — Windows cmd.exe 사용자는 `chcp 65001`이나 Windows Terminal 사용을 권장.

## 트러블슈팅

| 증상 | 원인 / 해결 |
|---|---|
| `Error: claude not found` | Claude Code CLI(`https://claude.ai/code`) 설치 후 PATH에 등록. |
| `Error: git not found` | git 2.10+ 설치. worktree 기반 병렬 실행에 필수. |
| 실행이 시작되자마자 "no pending tasks"로 종료 | 모든 task가 `done: true`. `ralph --reset` 또는 `tasks.json` 직접 수정. |
| worktree 생성이 "already exists"로 실패 | 이전 실행에서 남은 worktree. `ralph --worktree-cleanup` (또는 `.ralph-worktrees/{taskId}` 제거 후 `git worktree prune`). |
| 브랜치에 uncommitted 변경이 있어 worktree 차단 | 내용 확인 후 직접 commit/머지하거나 `ralph --worktree-cleanup`으로 강제 제거. |
| Claude 호출이 무한히 멈춤 | `--task-timeout 30m` 등 적절한 값 설정. timeout 시 process tree 강제 종료. |
| `--budget-usd` 초과 사용 | 정상 — 게이트는 새 dispatch만 막고 in-flight는 못 막는다. 더 타이트하게 막으려면 `maxConcurrent`를 낮추자. |
| 첫 배치 smoke test 실패 | `.ralph-logs/` 확인. 머지 결과를 직접 수정(자동 롤백 없음)하거나, `workflow.smokeTest`를 더 타게팅된 명령으로 바꾸거나, 반복 작업 중에는 `--no-smoke-test`. |
| 실행 도중 `tasks.json`을 변경했음 | Ralph는 머지 사이에 `tasks.json`을 reload하지만, `--run` 활성 중에는 직접 수정하지 말 것. 깨끗한 상태가 필요하면 `--reset`. |
| 검증이 계속 재시도됨 | `workflow.verifyRetries`를 낮추자(0이면 즉시 실패). 실제 명령 출력은 `validation.jsonl` 참고. |

## 보안

다음 파일 패턴은 auto-commit에서 자동 제외되며 `--validate`에서 경고된다:

`.env`, `.env.*`, `*.pem`, `*.key`, `*.p12`, `*.pfx`, `credentials.json`, `service-account*.json`, `.secret*`, `*.secrets`, `id_rsa`, `id_ed25519`

민감 파일이 감지되면 Ralph는 경고를 띄운다. **이 점검은 방어선이 아니라 tripwire로 다뤄야 한다 — Claude는 `--dangerously-skip-permissions`로 실행되며 호스트 사용자가 읽을 수 있는 모든 것을 읽을 수 있다.** 신뢰할 수 없는 plan은 격리 환경에서 실행하라.

## 기여 및 개발

```bash
# 빌드
dotnet build ralph.sln

# 테스트
dotnet test ralph.sln

# 현재 OS용 self-contained 바이너리 publish
dotnet publish Ralph/Ralph.csproj -c Release -r osx-arm64 --self-contained true

# 릴리스 스크립트 (gh CLI 사용). 최신 태그를 기준으로 버전을 자동 산출하고
# 태그 생성/푸시, 플랫폼별 바이너리 빌드, claude CLI로 영문/한글 릴리스 노트
# 자동 작성, GitHub에 업로드까지 일괄 수행한다.
./release-binary.sh                  # POSIX 호스트
./release-binary.ps1                 # Windows 호스트 (PowerShell 7+)
```

repo 레이아웃:

- `Ralph/` — 메인 프로젝트 (Program.cs + Commands/ + Services/ + Models/).
- `Ralph.Tests/` — xUnit 테스트 프로젝트.
- `ralph-schema.json`, `pricing.json` — 임베드 리소스.
- `samples/PRD.md` — 계산기 데모용 예시 PRD.
- `bugfix.md`, `enhance1.md` — Ralph로 Ralph를 빌드한 과정에서 사용한 historical PRD.

LLM 기여자를 위한 서비스 단위 아키텍처 맵은 `CLAUDE.md` 참고.

## GitHub Topics

이 저장소를 fork해서 운영한다면 검색성을 위해 다음 GitHub topic을 추가하자. Topic은 저장소 소유자가 GitHub 웹 UI에서 직접 설정해야 한다 (저장소 홈의 "About" 섹션 톱니바퀴 → "Topics" 필드):

- `ralph-loop`
- `agentic-ai`
- `ai-coding`
- `prd`
- `task-orchestrator`
- `claude-code`
- `autonomous-agent`
- `parallel-execution`
