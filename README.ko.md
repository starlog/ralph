# Ralph

[English](README.md) | **한국어**

PRD(Product Requirements Document) 기반 작업 계획을 생성하고, Claude Code를 통해 **병렬로** 자동 실행하는 CLI 태스크 오케스트레이터.
.NET 8로 구현 (Windows, macOS, Linux 크로스플랫폼).

**git worktree 기반 병렬 실행을 최초로 구현한 Ralph 변종.** 독립적인 기능들에 대해 여러 Claude Code 에이전트를 동시에 실행하며, 자동 의존성 해결, 충돌 fallback chain, exit code 기반 검증 게이트, 누적 비용 게이트, 실시간 진행 모니터링을 제공한다.

## ⚠️ 보안 주의

Ralph는 호스트 머신에서 Claude Code를 직접 실행한다. 신뢰할 수 없는 PRD나 외부 `tasks.json` 파일은 격리된 환경(별도 사용자 계정, VM 또는 컨테이너)에서 실행해야 한다. 다음과 같은 정보가 노출될 수 있다:

- `~/.ssh`, `~/.aws`, `~/.config` 내 자격 증명
- 환경변수에 저장된 API 키
- 호스트의 모든 파일에 대한 읽기 권한

## 동작 원리

Ralph는 기능 단위로 **4단계 패턴**을 따른다:

```
plan → implementation → testing → commit
```

각 기능(feature)마다 위 4개의 태스크가 생성되며, 의존성 체인으로 연결되어 순서가 보장된다. 독립적인 기능들은 git worktree 기반으로 **병렬 실행**된다.

```
user-auth-plan ─→ user-auth-impl ─→ user-auth-test ─→ user-auth-commit ─┐
                                                                          ├─→ main-plan ─→ ...
payment-plan ─→ payment-impl ─→ payment-test ─→ payment-commit ──────────┘
  (병렬 실행)                                                    (병합 후 순차)
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
| v0.1 | `ralph.sh` (Bash) | macOS, Linux | 순차 실행 |
| v0.6 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | 병렬 실행, worktree, live log |
| v0.7 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | `--graph` 태스크 의존성 그래프 |
| v1.0 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | 비용 추적, 플랜 검증, prompt builder, webhook 알림, 로그 로테이션 |
| v1.1 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | 검증 게이트, 충돌 전략 chain, `--task-timeout`, `--budget-usd`, `--strict-files`, 머지 직전 worktree rebase |

## 필수 의존성

| 도구 | 설명 |
|---|---|
| [Claude Code](https://claude.ai/code) | Claude Code CLI |
| [git](https://git-scm.com/) | 버전 관리 (worktree 기반 병렬 실행에 필요) |

## 설치

### 방법 1: 설치 스크립트 (소스에서 빌드)

.NET 8 SDK가 필요하다. 스크립트가 자동으로 빌드하고 PATH에 설치한다.

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
curl -LO https://github.com/starlog/ralph/releases/latest/download/ralph-v1.1.0-linux-x64.tar.gz
tar -xzf ralph-v1.1.0-linux-x64.tar.gz
sudo mv ralph /usr/local/bin/
```

바이너리는 자체 포함(self-contained)이므로 .NET 런타임 설치가 필요 없다.

### 방법 3: 패키지 매니저

```bash
# macOS / Linux — Homebrew tap
brew tap starlog/ralph https://github.com/starlog/ralph
brew install ralph

# Windows — Scoop (custom manifest)
scoop install https://raw.githubusercontent.com/starlog/ralph/main/scoop/ralph.json
```

매니페스트는 [`Formula/ralph.rb`](Formula/ralph.rb), [`scoop/ralph.json`](scoop/ralph.json)에 위치하며 최신 GitHub Release를 가리킨다.

## 사용법

### 기본 워크플로우

```bash
# 1. PRD에서 작업 계획 생성 (atomic write)
ralph --plan docs/PRD.md

# 2. 생성된 플랜 검증 (cycle / dangling deps / 파일 충돌 등)
ralph --validate

# 3. 생성된 작업 확인
ralph --list

# 4. 실행 미리보기 (실제 변경 없음)
ralph --dry-run

# 5. 전체 작업 자동 실행
ralph --run
```

### 전체 명령어

| 명령어 | 설명 |
|---|---|
| `--plan <파일>` | PRD 파일을 분석하여 `tasks.json` 생성 (atomic write) |
| `--plan-prompt <파일>` | 실제 실행 없이 plan 프롬프트 전체를 출력 |
| `--validate` | `tasks.json` 검증 (cycle, dangling deps, 중복 ID, 파일 충돌, 민감 경로) |
| `--run [파일]` | 모든 pending 작업 실행 (병렬 모드 기본). 파일 미지정 시 `tasks.json` 사용 |
| `--dry-run [파일]` | 실행 시뮬레이션 (종료 시 `tasks.json` 자동 복원) |
| `--task <id>` | 특정 작업 하나만 실행 (`--force`로 의존성 검사 우회) |
| `--interactive` | 대화형 모드 — 각 작업마다 확인 후 실행 |
| `--list`, `-l` | pending 작업 목록 출력 (병렬 실행 가능 여부 표시) |
| `--graph`, `-g` | ASCII 태스크 의존성 그래프 출력 (병렬/순차 구조 시각화) |
| `--prompts`, `-p` | 모든 작업의 Claude 프롬프트 출력 |
| `--show-prompt <id>` | 특정 작업에 전송될 전체 프롬프트 출력 |
| `--status`, `-s` | 진행 상황 대시보드 (병렬 배치 정보 포함) |
| `--cost` | 누적 토큰 사용량 / 추정 USD 비용 출력 |
| `--reset`, `-r` | 모든 작업을 pending으로 초기화 |
| `--logs` | 로그 파일 목록 (세션 + 태스크) |
| `--logs <task-id>` | 특정 태스크 로그 출력 |
| `--logs --live <task-id>` | 태스크 로그 실시간 추적 (tail -f) |
| `--logs --cleanup` | retention 기간을 지난 로그 삭제 |
| `--worktree-cleanup` | 잔존 worktree 정리 |
| `--help`, `-h` | 도움말 |

### 실행 옵션

| 옵션 | 설명 |
|---|---|
| `-f`, `--file <경로>` | 커스텀 tasks 파일 사용 (대부분 명령어와 호환) |
| `--sequential` | 병렬 실행 비활성화, 순차 실행 강제 |
| `--max-parallel N` | 최대 동시 실행 태스크 수 지정 |
| `--force` | 의존성 / 검증 우회 (`--task` 또는 `--run`과 함께) |
| `--strict-files` | 머지 후 declared `modifiedFiles` 외 파일 변경이 있으면 abort |
| `--shared-worktrees` | `git worktree add --shared`로 `.git` objects를 공유해 디스크/IO 절약 (미지원 시 자동 fallback) |
| `--budget-usd <amt>` | 누적 비용이 amt(USD) 도달 시 새 태스크 dispatch 중단 |
| `--task-timeout <기간>` | Claude 호출당 timeout (예: `30m`, `1h`, `90s`, `1800`) — hang 방지 |
| `--llm-critique` | `--plan` 직후 LLM 기반 PRD/plan 비평 1회 추가 실행 (기본 off, 추가 LLM 호출 비용) |
| `--model <name>` | 모델 선택 (`sonnet`, `opus`; 기본: `opus`) |
| `--debug` | Claude stream 이벤트 출력 |

### 커스텀 tasks.json 파일 사용

두 가지 방법:

```bash
ralph --run my-project-tasks.json     # 위치 인자 (run/dry-run/list/graph 등)
ralph -f my-project-tasks.json --run  # 글로벌 -f / --file 플래그
```

### 대화형 모드

`--interactive`로 실행하면 각 작업마다 선택지가 표시된다:

- `Yes - Execute` — 실행
- `Preview prompt` — 프롬프트 미리보기
- `Skip` — 건너뛰기
- `Quit` — 종료

### 환경 변수

| 변수 | 기본값 | 설명 |
|---|---|---|
| `MAX_RETRIES` | 2 | Claude Code 실행 실패 시 재시도 횟수 |
| `RETRY_DELAY` | 5 | 재시도 간 대기 시간 (초) |
| `RALPH_MAX_PARALLEL` | 0 (tasks.json 설정 사용) | 최대 동시 실행 태스크 수 오버라이드 |
| `RALPH_PARALLEL` | true | `false`로 설정 시 병렬 실행 비활성화 |
| `RALPH_STRICT_FILES` | false | `true`로 설정 시 `--strict-files` 기본 활성화 |
| `RALPH_SHARED_WORKTREES` | false | `true`로 설정 시 `--shared-worktrees` 기본 활성화 |
| `RALPH_BUDGET_USD` | (없음) | 누적 비용 임계값 — CLI `--budget-usd`가 우선 |
| `RALPH_TASK_TIMEOUT_SEC` | (없음) | Claude 호출당 timeout(초) — CLI `--task-timeout`가 우선 |
| `RALPH_WEBHOOK_URL` | (없음) | 세션 종료 webhook 기본 URL |
| `RALPH_LOG_RETENTION_DAYS` | 30 | N일보다 오래된 로그 자동 삭제 |

공유 설정의 우선순위: CLI 플래그 > 환경변수 > `tasks.json`의 `workflow` 설정 > 기본값.

```bash
# Linux/macOS
MAX_RETRIES=3 ralph --run
RALPH_MAX_PARALLEL=4 ralph --run
RALPH_BUDGET_USD=10.00 ralph --run
RALPH_TASK_TIMEOUT_SEC=1800 ralph --run

# Windows (PowerShell)
$env:MAX_RETRIES=3; ralph --run
$env:RALPH_PARALLEL="false"; ralph --run    # 순차 실행 강제
```

## 프로젝트 구조

```
ralph/
├── Ralph/                          # .NET 8 프로젝트 (v1.1)
│   ├── Ralph.csproj                # 프로젝트 설정 (단일 파일, self-contained)
│   ├── Program.cs                  # CLI 진입점 및 명령어 처리
│   ├── Models/
│   │   ├── TasksFile.cs            # tasks.json 모델 (TaskItem, SubTask, ParallelConfig 등)
│   │   └── RalphJsonContext.cs     # JSON 소스 생성기 (IL 트리밍 호환)
│   └── Services/
│       ├── PlanGenerator.cs        # PRD → tasks.json 생성 (atomic write)
│       ├── PlanValidator.cs        # tasks.json 무결성 검증
│       ├── PromptBuilder.cs        # task 실행 프롬프트 조립
│       ├── ClaudeService.cs        # Claude Code 프로세스 실행 / 스트리밍 / timeout
│       ├── TaskManager.cs          # tasks.json 로드/저장/쿼리/의존성 DAG
│       ├── ParallelExecutor.cs     # Worktree 기반 병렬 실행 엔진 + 충돌 chain
│       ├── WorktreeService.cs      # Git worktree 생성/머지 직전 rebase/병합/정리
│       ├── VerificationRunner.cs   # exit code 기반 외부 검증 + 1회 self-fix
│       ├── CostTracker.cs          # token 사용량 / USD 비용 누적 (.ralph-logs/cost.jsonl)
│       ├── BudgetGate.cs           # 누적 비용 임계값 게이트
│       ├── NotificationService.cs  # Slack/Discord/generic webhook 세션 알림
│       ├── LogRotator.cs           # 오래된 로그 정리 (cost.jsonl/validation.jsonl 보존)
│       ├── DurationParser.cs       # "30m"/"1h"/"90s" 파서
│       ├── GitService.cs           # Git 커밋, 초기 커밋 보장, 안전한 stdout/stderr 파이프
│       ├── GraphRenderer.cs        # ASCII 태스크 의존성 그래프 렌더링
│       ├── TaskProgressTracker.cs  # 병렬 실행 실시간 진행 상황 표시
│       └── RalphLogger.cs          # thread-safe 파일 로거
├── Ralph.Tests/                    # xUnit 테스트 프로젝트
├── samples/                        # 예제 파일
│   └── PRD.md                      # 병렬 실행 예제 PRD (CLI 계산기)
├── install.sh                      # macOS/Linux 설치 스크립트
├── install.ps1                     # Windows 설치 스크립트 (PowerShell)
├── ralph.sh                        # (레거시) Bash 버전 v0.1
├── ralph-schema.json               # JSON Schema (빌드 시 바이너리에 embed)
├── pricing.json                    # 모델별 단가 (바이너리 embed; ~/.ralph/pricing.json로 override 가능)
├── CLAUDE.md                       # Claude Code 가이드
└── README.md
```

## tasks.json 구조

`ralph --plan`으로 자동 생성되거나 직접 작성할 수 있다. 스키마는 `ralph-schema.json`에 정의되어 있고 바이너리에 embed된다.

### 최소 예시

```json
{
  "projectName": "my-project",
  "version": "1.0.0",
  "tasks": [
    {
      "id": "setup-plan",
      "title": "프로젝트 초기 설정 계획",
      "done": false,
      "phase": "phase1-setup",
      "category": "plan",
      "prompt": "프로젝트 구조를 분석하고 초기 설정 계획을 수립하세요...",
      "outputFiles": ["docs/setup-plan.md"]
    }
  ]
}
```

### 전체 구조

```json
{
  "projectName": "프로젝트 이름",
  "version": "1.0.0",
  "workflow": {
    "onTaskComplete": {
      "commitChanges": true,
      "commitMessageTemplate": "[Task #{taskId}] {taskTitle}"
    },
    "parallel": {
      "enabled": true,
      "maxConcurrent": 5,
      "conflictStrategies": ["auto-theirs", "claude"]
    },
    "notifications": {
      "onComplete": "https://hooks.slack.com/services/XXX",
      "format": "slack"
    },
    "logRetentionDays": 30,
    "budgetUsd": 10.00,
    "taskTimeoutSec": 1800,
    "maxRetries": 2,
    "retryDelay": 5
  },
  "apiSpecs": { ... },
  "samplePages": { ... },
  "tasks": [ ... ]
}
```

### task 객체

| 속성 | 필수 | 타입 | 설명 |
|---|---|---|---|
| `id` | **필수** | string | 고유 ID. kebab-case (`^[a-zA-Z0-9_-]+$`) |
| `title` | **필수** | string | 작업 제목 (최대 200자) |
| `done` | **필수** | boolean | 완료 여부. 실행 시 자동으로 `true`로 변경 |
| `description` | | string | 상세 설명 |
| `phase` | | string | 프로젝트 단계 (예: `"phase1"`, `"phase2"`) |
| `category` | | string | 카테고리 (예: `"plan"`, `"implementation"`, `"testing"`, `"commit"`) |
| `prompt` | | string | Claude Code에 전달할 프롬프트. 없으면 Claude 실행 생략 |
| `outputFiles` | | string[] | 생성/수정 예상 파일 경로 목록 |
| `modifiedFiles` | | string[] | 수정 대상 파일. 병렬 실행 시 머지 충돌 감지와 `--strict-files` 검증에 사용 |
| `dependsOn` | | string[] | 선행 작업 ID 배열. 모두 완료되어야 실행 가능. 없으면 병렬 실행 대상 |
| `subtasks` | | array | 하위 작업 배열 |
| `verification` | | object | `{ command, timeoutSec? }` — exit code 기반 외부 검증 (아래 검증 게이트 참고) |

### subtask 객체

| 속성 | 필수 | 타입 | 설명 |
|---|---|---|---|
| `id` | **필수** | string | 하위 작업 고유 ID |
| `title` | **필수** | string | 하위 작업 제목 |
| `done` | **필수** | boolean | 완료 여부 |
| `prompt` | | string | 하위 작업 전용 프롬프트 |

### workflow 설정

| 설정 | 기본값 | 설명 |
|---|---|---|
| `onTaskComplete.commitChanges` | true | task 완료 후 자동 `git add -A && git commit` |
| `onTaskComplete.commitMessageTemplate` | — | `{taskId}`, `{taskTitle}` 플레이스홀더 사용 가능 |
| `parallel.enabled` | true | 병렬 실행 활성화 |
| `parallel.maxConcurrent` | 5 | 최대 동시 실행 수 (상한 16) |
| `parallel.conflictStrategy` | `"claude"` | 단일 전략 (legacy, `conflictStrategies`가 없을 때만 사용) |
| `parallel.conflictStrategies` | (없음) | 충돌 fallback chain — 있으면 `conflictStrategy`보다 우선 |
| `notifications.onComplete` / `onFailure` | (없음) | 세션 webhook URL |
| `notifications.format` | auto | `generic` / `slack` / `discord` |
| `logRetentionDays` | 30 | `.ralph-logs/`에서 N일보다 오래된 로그 자동 삭제 (cost/validation은 보존) |
| `budgetUsd` | (없음) | 누적 비용 임계값 — CLI/env가 우선 |
| `taskTimeoutSec` | (없음) | Claude 호출당 timeout — CLI/env가 우선 |
| `maxRetries` | 2 | Claude 호출당 재시도 횟수 (env `MAX_RETRIES`가 우선) |
| `retryDelay` | 5 | 재시도 간 대기 (env `RETRY_DELAY`가 우선) |

### apiSpecs / samplePages

작업 프롬프트에서 참조할 수 있는 보조 정보:

```json
{
  "apiSpecs": {
    "createUser": {
      "method": "POST",
      "endpoint": "/api/users",
      "description": "사용자 생성 API",
      "requestBody": { ... },
      "responseBody": { ... }
    }
  },
  "samplePages": {
    "loginPage": {
      "url": "/login",
      "description": "로그인 페이지"
    }
  }
}
```

## 병렬 실행

Ralph는 독립적인 태스크를 git worktree를 이용하여 병렬로 실행한다. 핵심은 **의존성 그래프** — `dependsOn`이 없는 태스크들은 동시에 실행할 수 있다.

### 동작 방식

```
ralph --run
```

1. 의존성 DAG를 분석하여 즉시 실행 가능한 태스크들을 배치로 그룹화
2. 태스크별 git worktree 생성 (`ralph/{taskId}` 브랜치, `.ralph-worktrees/` 디렉토리)
3. 각 worktree에서 Claude Code를 동시에 실행 (실시간 진행 대시보드 표시)
4. 정의된 경우 `verification.command` 실행 — 실패 시 1회 self-fix 재시도
5. 머지 직전 worktree 브랜치를 최신 base로 rebase (advance)
6. 완료된 브랜치를 순차적으로 base 브랜치에 병합
7. 머지 충돌 시 `conflictStrategies` chain을 순서대로 시도
8. (`--strict-files`) 머지 결과가 declared `modifiedFiles` 안에 들어오는지 검증
9. 다음 배치로 진행 (새로 의존성이 충족된 태스크들)
10. 단일 태스크만 남으면 worktree 없이 직접 실행

### 병렬 실행을 위한 PRD 작성 가이드

`ralph --plan`이 병렬 실행에 최적화된 `tasks.json`을 생성하도록 하려면, PRD에서 **독립적인 기능을 명확히 분리**해야 한다.

#### 핵심 원칙

**독립적인 기능** = 서로 다른 파일을 수정하고, 서로의 코드를 참조하지 않는 기능

Ralph의 plan generator는 다음 규칙으로 의존성을 결정한다:
- 같은 기능 내 4단계(plan→impl→test→commit)는 항상 순차적
- **다른 기능 간 `dependsOn`이 없으면** → 병렬 실행 가능
- **다른 기능의 결과물을 사용하면** → `dependsOn`으로 연결 (순차)

#### 좋은 PRD 구조 (병렬 최대화)

기능을 독립된 모듈로 나누고, 공유 기반(shared foundation)은 별도 phase로 분리한다:

```markdown
# PRD: 계산기 앱

## Phase 1 — 연산 모듈 (각각 독립적, 병렬 실행 가능)

### 덧셈 모듈
- `add.py` 파일에 add(a, b) 함수 구현
- `tests/test_add.py`에 테스트 작성

### 뺄셈 모듈
- `subtract.py` 파일에 subtract(a, b) 함수 구현
- `tests/test_subtract.py`에 테스트 작성

### 곱셈 모듈
- `multiply.py` 파일에 multiply(a, b) 함수 구현
- `tests/test_multiply.py`에 테스트 작성

### 나눗셈 모듈
- `divide.py` 파일에 divide(a, b) 함수 구현 (0 나누기 예외 처리)
- `tests/test_divide.py`에 테스트 작성

## Phase 2 — 메인 진입점 (Phase 1 완료 후)

### CLI 메인
- `main.py`에서 위 4개 모듈을 import하여 CLI 인터페이스 구현
- 모든 연산 모듈이 완료된 후 구현해야 함

## Phase 3 — 통합 테스트 (Phase 2 완료 후)

### 통합 테스트
- 전체 시스템 통합 테스트 작성
```

이렇게 작성하면 생성되는 실행 구조:

```
                    ┌─ add-plan → add-impl → add-test → add-commit ────────┐
                    ├─ subtract-plan → subtract-impl → ... → subtract-commit ┤
ralph --run ────────┤                                                        ├─→ main-plan → ... → main-commit ─→ integration-plan → ...
                    ├─ multiply-plan → multiply-impl → ... → multiply-commit ┤
                    └─ divide-plan → divide-impl → ... → divide-commit ─────┘
                         (4개 동시 실행)                         (병합)            (순차)                     (순차)
```

#### PRD에서 병렬 실행을 유도하는 팁

| 전략 | 설명 |
|---|---|
| **파일 분리 명시** | 각 기능이 수정하는 파일을 PRD에 명시하면 `modifiedFiles`가 정확하게 생성됨 |
| **Phase 분리** | 독립 기능은 같은 Phase에, 의존 기능은 다음 Phase에 배치 |
| **"독립적", "병렬" 키워드** | PRD에 "각각 독립적으로 구현 가능" 같은 힌트를 추가 |
| **공유 코드 최소화** | 공통 유틸리티는 첫 Phase에서 만들고, 이후 기능들이 의존하도록 구조화 |
| **기능 간 의존 명시** | "X 모듈은 Y 완료 후 구현" 같은 의존 관계를 명확히 기술 |

#### 나쁜 예 (병렬 불가)

모든 기능이 같은 파일을 수정하거나, 의존 관계가 불명확한 경우:

```markdown
# 나쁜 PRD 예시
## 기능 1: 사용자 인증
- app.py에 로그인 기능 추가

## 기능 2: 사용자 프로필
- app.py에 프로필 기능 추가    ← 같은 파일! 병합 충돌 발생

## 기능 3: 대시보드
- app.py에 대시보드 기능 추가   ← 같은 파일! 병합 충돌 발생
```

→ 이 경우 Ralph가 `dependsOn`을 걸거나, 병렬 실행 후 머지 충돌이 발생한다.

**개선:** 각 기능을 별도 파일/모듈로 분리하도록 PRD를 작성한다.

### 실패 처리와 재개

병렬 배치 일부가 실패했을 때 동작:

| 상황 | 동작 |
|---|---|
| 배치 안 한 task의 Claude 실행 실패 | 같은 배치의 **다른 task는 계속 진행하고 머지**된다. 실패한 task의 worktree만 정리되고 `done` 플래그는 그대로 `false`. |
| `verification.command` 실패 | 1회 self-fix 재시도 (`workflow.verifyRetries`로 횟수 조정 가능). 재시도도 실패하면 task 실패로 마킹되며 **머지에서 제외**. |
| pre-commit scope 위반 (`--strict-files`) | 머지 전 worktree 단계에서 fail-fast — 정리 비용 절감. 같은 배치의 다른 task는 영향 없음. |
| 전략 chain으로도 풀지 못한 머지 충돌 | 미머지 worktree만 정리되고 **이미 머지된 task는 그대로 유지**된다 (자동 rollback 없음). |
| 머지 후 `workflow.smokeTest` 실패 | 종료 코드 1로 중단. 이미 머지된 변경은 되돌리지 않으며 실패가 로그·콘솔에 표시. |

**중단 후 재개:**
- `done: true`는 task 단위 atomic write이므로 `ralph --run`을 다시 실행하면 미완료(`done: false`)인 task만 dispatch된다 — 정확히 멈춘 지점부터 이어진다.
- `--run` 시작 시 worktree에 uncommitted 변경 또는 base 위 커밋이 있으면 Ralph는 **자동으로 삭제하지 않는다**. worktree 경로를 보여주고 사용자가 머지/회수 또는 `ralph --worktree-cleanup`으로 강제 삭제하도록 안내한다.
- 변경이 없는(clean) 잔존 worktree는 자동 정리.

**이미 머지된 task는 자동으로 되돌리지 않는다.** 머지를 commit point로 보는 설계 — 되돌리려면 사용자가 직접 `git revert` / `git reset` 해야 한다. 머지가 영구화되기 전에 잡으려면 `--strict-files`와 `workflow.smokeTest`를 활용.

**Smoke test는 opt-out.** `workflow.smokeTest`를 명시하지 않으면 Ralph가 repo root marker로 자동 추론한다 (`*.csproj`/`*.sln` → `dotnet build`, `package.json` → `npm test`, `Cargo.toml` → `cargo build`, `go.mod` → `go build`). 명시 지정된 `workflow.smokeTest`는 항상 우선한다. 완전히 비활성화하려면 `--no-smoke-test` 또는 `RALPH_NO_SMOKE_TEST=true`.

### 충돌 해결 전략

`workflow.parallel.conflictStrategies` (chain) 또는 legacy `workflow.parallel.conflictStrategy` (단일)에서 설정한다. chain은 **순서가 있는 fallback list** — 첫 항목이 초기 머지의 `-X` 플래그(auto-* 인 경우)를 결정하고, 머지 또는 직전 단계가 실패하면 나머지 항목을 순서대로 시도한다.

| 전략 | 동작 |
|---|---|
| `claude` | Claude Code가 충돌 마커를 분석하여 양쪽 변경사항을 병합 (chain 종단으로 권장) |
| `abort` | 병합 중단 후 해당 태스크를 순차 모드로 재실행 |
| `auto-theirs` | git의 `-X theirs` — worktree 브랜치의 변경사항 우선 |
| `auto-ours` | git의 `-X ours` — base 브랜치의 변경사항 우선 |

예시 — 사소한 충돌은 `-X theirs`로 자동 머지하고, `-X theirs`가 해결할 수 없는 경우(add/add, rename/delete)에만 Claude로 escalate:

```json
"conflictStrategies": ["auto-theirs", "claude"]
```

### 검증 게이트 (verification)

각 태스크에 `verification.command`를 정의하면, Ralph는 그 exit code를 ground truth로 본다 — Claude의 self-report는 무시한다. exit code가 0이 아니면 Ralph가 stdout/stderr를 Claude에게 다시 넘기고 **1회 self-fix retry**를 한 뒤 실패 처리한다.

```json
{
  "id": "math-impl",
  "verification": { "command": "go test ./...", "timeoutSec": 120 }
}
```

흔히 쓰는 명령: `pytest tests/`, `go test ./...`, `tsc --noEmit`, `dotnet test`, `npm test --silent`, `cargo test --quiet`.

### 비용 추적 / 예산 게이트

Claude의 `stream-json` `result` 이벤트에 들어오는 호출별 토큰/비용 정보가 `.ralph-logs/cost.jsonl`에 누적 기록된다. `--budget-usd <amt>`(또는 `RALPH_BUDGET_USD`)를 지정하면 누적 비용이 임계값에 도달했을 때 새 dispatch를 중단하며, 80% 도달 시 1회 경고가 뜬다.

```bash
ralph --cost                            # 누적 토큰/USD 출력
ralph --run --budget-usd 5.00           # $5 도달 시 새 태스크 중단
```

단가는 embed된 `pricing.json`에서 로드한다. `~/.ralph/pricing.json`을 두면 override.

### Webhook 알림

세션 종료 시 한 번 webhook을 보낸다. 우선순위:

1. `workflow.notifications.onComplete` / `onFailure` (tasks.json)
2. `RALPH_WEBHOOK_URL` 환경변수 (전역 fallback)

`format`은 호스트명으로 자동 감지(`hooks.slack.com` → Slack, `discord(app)?.com` → Discord, 그 외 → generic)되며 `workflow.notifications.format`으로 강제 지정 가능하다.

### `modifiedFiles`의 역할

각 태스크의 `modifiedFiles` 필드는 해당 태스크가 수정할 파일 목록이다. PRD에서 파일 경로를 명시하면 plan generator가 이 필드를 정확하게 생성한다. `--strict-files` (또는 `RALPH_STRICT_FILES=true`)를 켜면 머지 후 declared 외 파일이 변경되어 있을 때 abort한다.

```json
{
  "id": "add-impl",
  "title": "덧셈 모듈 구현",
  "modifiedFiles": ["add.py", "tests/test_add.py"],
  "dependsOn": ["add-plan"]
}
```

### 실시간 모니터링

병렬 실행 중 다른 터미널에서 태스크 로그를 실시간으로 확인할 수 있다:

```bash
# 터미널 1: 실행
ralph --run

# 터미널 2: 특정 태스크 로그 실시간 추적
ralph --logs --live add-impl     # Ctrl+C로 종료

# 터미널 3: 다른 태스크 로그 추적
ralph --logs --live subtract-impl
```

### 병렬 실행 확인

```bash
# 태스크 의존성 그래프 시각화
ralph --graph

# 병렬 배치 구조 미리보기
ralph --status
```

## 의존성 관리

`dependsOn`으로 작업 간 실행 순서를 제어한다. 선행 작업이 모두 `done: true`가 되어야 해당 작업이 실행 가능하며, **`dependsOn`이 없는 태스크들은 병렬 실행 대상**이 된다.

```json
{
  "tasks": [
    { "id": "auth-plan", "title": "인증 설계", "done": false },
    { "id": "auth-impl", "title": "인증 구현", "done": false, "dependsOn": ["auth-plan"] },
    { "id": "auth-test", "title": "인증 테스트", "done": false, "dependsOn": ["auth-impl"] },
    { "id": "auth-commit", "title": "인증 커밋", "done": false, "dependsOn": ["auth-test"] },

    { "id": "payment-plan", "title": "결제 설계", "done": false },
    { "id": "payment-impl", "title": "결제 구현", "done": false, "dependsOn": ["payment-plan"] }
  ]
}
```

위 예시에서 `auth-plan`과 `payment-plan`은 `dependsOn`이 없으므로 동시에 실행된다.

`ralph --task <id>`는 기본적으로 `dependsOn`을 검사하며, 미완료 의존성이 있으면 차단된다. `--force`로 우회할 수 있다.

## 로그

실행 로그는 `.ralph-logs/` 디렉토리에 저장된다:

```
.ralph-logs/
├── ralph-20260219-165209.log   # 세션 로그
├── add-plan.log                # 태스크별 로그 (병렬 실행 시)
├── subtract-plan.log
├── multiply-plan.log
├── cost.jsonl                  # 누적 토큰/비용 ledger (rotation 시 보존)
└── validation.jsonl            # verification 명령 ledger (rotation 시 보존)
```

```bash
# 로그 파일 목록
ralph --logs

# 특정 태스크 로그 보기
ralph --logs add-impl

# 실시간 로그 추적 (병렬 실행 중 모니터링)
ralph --logs --live add-impl

# retention 기간을 지난 로그 삭제 (기본 30일)
ralph --logs --cleanup
```

## 예제

`samples/` 디렉토리에 Ralph 사용 예제가 포함되어 있다.

### samples/PRD.md — CLI 계산기

병렬 실행에 최적화된 PRD 예제. Python 사칙연산 계산기를 구현하며, 다음 구조를 보여준다:

- **Phase 1** — 4개 연산 모듈(`add.py`, `subtract.py`, `multiply.py`, `divide.py`)이 각각 독립적이므로 **병렬 실행**
- **Phase 2** — `main.py`가 4개 모듈을 모두 import하므로 Phase 1 완료 후 **순차 실행**
- **Phase 3** — 통합 테스트, Phase 2 완료 후 실행

```bash
# 예제 실행 방법
mkdir my-calculator && cd my-calculator
cp /path/to/ralph/samples/PRD.md .

ralph --plan PRD.md       # 24개 태스크 생성 (4개 병렬 시작점)
ralph --validate          # 생성된 플랜 sanity check
ralph --status            # 병렬 배치 구조 확인
ralph --run               # 실행 (Phase 1은 4개 동시, Phase 2~3은 순차)
```

이 PRD의 핵심 포인트:
- 각 모듈이 **별도 파일**을 수정하므로 머지 충돌 없이 병렬 실행 가능
- Phase와 의존성을 **명시적으로 기술**하여 plan generator가 정확한 `dependsOn`을 생성
- `"병렬 실행 가능"` 힌트를 PRD에 포함하여 병렬 구조 유도

## 보안

커밋 시 다음 패턴의 파일은 자동으로 제외되며 `--validate`에서 경고된다:

`.env`, `.env.*`, `*.pem`, `*.key`, `*.p12`, `*.pfx`, `credentials.json`, `service-account*.json`, `.secret*`, `*.secrets`, `id_rsa`, `id_ed25519`

제외된 민감 파일이 감지되면 경고 메시지가 출력된다.
