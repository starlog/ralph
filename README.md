# Ralph

PRD(Product Requirements Document) 기반 작업 계획을 생성하고, Claude Code를 통해 **병렬로** 자동 실행하는 CLI 태스크 오케스트레이터.
.NET 8로 구현 (Windows, macOS, Linux 크로스플랫폼).

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

## 버전

| 버전 | 구현 | 플랫폼 | 주요 기능 |
|---|---|---|---|
| v0.1 | `ralph.sh` (Bash) | macOS, Linux | 순차 실행 |
| v0.6 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | 병렬 실행, worktree, live log |

## 설치

### .NET 8 버전 (v0.6, 권장)

#### 필수 의존성

| 도구 | 설치 |
|---|---|
| .NET 8 SDK | [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0) (빌드 시에만 필요) |
| claude | [Claude Code](https://claude.ai/code) 설치 |
| git | 기본 포함 |

#### 빌드 및 설치

```bash
# 빌드 (단일 파일 바이너리 생성)
cd Ralph
dotnet publish -c Release -r win-x64    # Windows
dotnet publish -c Release -r osx-x64    # macOS (Intel)
dotnet publish -c Release -r osx-arm64  # macOS (Apple Silicon)
dotnet publish -c Release -r linux-x64  # Linux

# 생성된 바이너리를 PATH에 복사
# Windows: Ralph/bin/Release/net8.0/win-x64/publish/ralph.exe
# macOS/Linux: Ralph/bin/Release/net8.0/{rid}/publish/ralph
```

빌드된 단일 바이너리(~14MB)에는 .NET 런타임이 포함되어 있어 별도 설치가 필요 없다.

### ~~Bash 버전 (v0.1, macOS/Linux 전용)~~ — 레거시, 사용 불필요

> **참고:** `ralph.sh`와 `install.sh`는 v0.1 Bash 구현의 잔존 파일로, 현재 .NET 8 버전(v0.6)에서는 **사용하지 않는다.** 병렬 실행, worktree, live log 등 최신 기능은 .NET 버전에만 포함되어 있다.

## 사용법

### 기본 워크플로우

```bash
# 1. PRD에서 작업 계획 생성
ralph --plan docs/PRD.md

# 2. 생성된 작업 확인
ralph --list

# 3. 실행 미리보기 (실제 변경 없음)
ralph --dry-run

# 4. 전체 작업 자동 실행
ralph --run
```

### 전체 명령어

| 명령어 | 설명 |
|---|---|
| `--plan <파일>` | PRD 파일을 분석하여 `tasks.json` 생성 |
| `--run [파일]` | 모든 pending 작업 실행 (병렬 모드 기본). 파일 미지정 시 `tasks.json` 사용 |
| `--dry-run` | 실행 시뮬레이션 (tasks.json 변경 없음) |
| `--task <id>` | 특정 작업 하나만 실행 |
| `--interactive` | 대화형 모드 — 각 작업마다 확인 후 실행 |
| `--list`, `-l` | pending 작업 목록 출력 (병렬 실행 가능 여부 표시) |
| `--prompts`, `-p` | 모든 작업의 Claude 프롬프트 출력 |
| `--status`, `-s` | 진행 상황 대시보드 (병렬 배치 정보 포함) |
| `--reset`, `-r` | 모든 작업을 pending으로 초기화 |
| `--logs` | 로그 파일 목록 (세션 + 태스크) |
| `--logs <task-id>` | 특정 태스크 로그 출력 |
| `--logs --live <task-id>` | 태스크 로그 실시간 추적 (tail -f) |
| `--worktree-cleanup` | 잔존 worktree 정리 |
| `--help`, `-h` | 도움말 |

### 실행 옵션

| 옵션 | 설명 |
|---|---|
| `--sequential` | 병렬 실행 비활성화, 순차 실행 강제 |
| `--max-parallel N` | 최대 동시 실행 태스크 수 지정 |

### 커스텀 tasks.json 파일 사용

`--run`에 파일 경로를 전달하면 기본 `tasks.json` 대신 해당 파일을 사용한다:

```bash
ralph --run my-project-tasks.json
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

```bash
# Linux/macOS
MAX_RETRIES=3 ralph --run
RALPH_MAX_PARALLEL=4 ralph --run

# Windows (PowerShell)
$env:MAX_RETRIES=3; ralph --run
$env:RALPH_PARALLEL="false"; ralph --run   # 순차 실행 강제
```

## 프로젝트 구조

```
ralph/
├── Ralph/                          # .NET 8 프로젝트 (v0.6)
│   ├── Ralph.csproj                # 프로젝트 설정 (단일 파일, self-contained)
│   ├── Program.cs                  # CLI 진입점 및 명령어 처리
│   ├── Models/
│   │   ├── TasksFile.cs            # tasks.json 모델 (TaskItem, SubTask, ParallelConfig 등)
│   │   └── RalphJsonContext.cs     # JSON 소스 생성기 (IL 트리밍 호환)
│   └── Services/
│       ├── ClaudeService.cs        # Claude Code 프로세스 실행 및 스트리밍
│       ├── TaskManager.cs          # tasks.json 로드/저장/쿼리/의존성 DAG
│       ├── GitService.cs           # Git 커밋 자동화, 초기 커밋 보장
│       ├── PlanGenerator.cs        # PRD → tasks.json 생성
│       ├── ParallelExecutor.cs     # Worktree 기반 병렬 실행 엔진
│       ├── WorktreeService.cs      # Git worktree 생성/병합/정리
│       ├── TaskProgressTracker.cs  # 병렬 실행 실시간 진행 상황 표시
│       └── RalphLogger.cs          # 파일 로깅
├── samples/                        # 예제 파일
│   └── PRD.md                      # 병렬 실행 예제 PRD (CLI 계산기)
├── ralph.sh                        # (레거시) Bash 버전 v0.1 — 사용 불필요
├── ralph-schema.json               # tasks.json JSON Schema
├── install.sh                      # (레거시) Bash 버전 설치 스크립트 — 사용 불필요
├── CLAUDE.md                       # Claude Code 가이드
└── README.md
```

## tasks.json 구조

`ralph --plan`으로 자동 생성되거나 직접 작성할 수 있다. 스키마는 `ralph-schema.json`에 정의되어 있다.

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
    }
  },
  "parallel": {
    "enabled": true,
    "maxConcurrent": 3,
    "conflictStrategy": "claude"
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
| `modifiedFiles` | | string[] | 수정 대상 파일 목록. 병렬 실행 시 병합 충돌 감지에 사용 |
| `dependsOn` | | string[] | 선행 작업 ID 배열. 해당 작업이 모두 완료되어야 실행 가능. 없으면 병렬 실행 대상 |
| `subtasks` | | array | 하위 작업 배열 |

### subtask 객체

| 속성 | 필수 | 타입 | 설명 |
|---|---|---|---|
| `id` | **필수** | string | 하위 작업 고유 ID |
| `title` | **필수** | string | 하위 작업 제목 |
| `done` | **필수** | boolean | 완료 여부 |
| `prompt` | | string | 하위 작업 전용 프롬프트 |

### workflow 설정

```json
{
  "onTaskComplete": {
    "commitChanges": true,
    "commitMessageTemplate": "[Task #{taskId}] {taskTitle}"
  }
}
```

- `commitChanges` — `true`이면 작업 완료 후 자동으로 `git add -A && git commit` 실행
- `commitMessageTemplate` — 커밋 메시지 템플릿. `{taskId}`와 `{taskTitle}` 플레이스홀더 사용 가능

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
4. 완료된 브랜치를 순차적으로 메인 브랜치에 병합
5. 병합 충돌 시 설정된 전략으로 처리
6. 다음 배치로 진행 (새로 의존성이 충족된 태스크들)
7. 단일 태스크만 남으면 worktree 없이 직접 실행

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

→ 이 경우 Ralph가 `dependsOn`을 걸거나, 병렬 실행 후 병합 충돌이 발생한다.

**개선:** 각 기능을 별도 파일/모듈로 분리하도록 PRD를 작성한다.

### tasks.json 병렬 설정

`ralph --plan`이 자동 생성하며, 수동으로도 설정 가능하다:

```json
{
  "parallel": {
    "enabled": true,
    "maxConcurrent": 3,
    "conflictStrategy": "claude"
  }
}
```

| 설정 | 기본값 | 설명 |
|---|---|---|
| `enabled` | true | 병렬 실행 활성화 |
| `maxConcurrent` | 3 | 최대 동시 실행 수 (worktree 수 = CPU/메모리에 따라 조절) |
| `conflictStrategy` | `"claude"` | 충돌 해결 전략 (아래 참조) |

#### 충돌 해결 전략

| 전략 | 동작 |
|---|---|
| `claude` | Claude Code가 충돌 마커를 분석하여 양쪽 변경사항을 병합 (권장) |
| `abort` | 병합 중단 후 해당 태스크를 순차 모드로 재실행 |
| `auto-theirs` | git의 `-X theirs` 전략 — worktree 브랜치의 변경사항 우선 |
| `auto-ours` | git의 `-X ours` 전략 — 메인 브랜치의 변경사항 우선 |

### `modifiedFiles`의 역할

각 태스크의 `modifiedFiles` 필드는 해당 태스크가 수정할 파일 목록이다. PRD에서 파일 경로를 명시하면 plan generator가 이 필드를 정확하게 생성한다.

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
# 병렬 배치 구조 미리보기
ralph --status

# 출력 예시:
# Total: 24 | Done: 0 | Ready: 4 | Blocked: 20
# 4개 태스크 병렬 실행 가능
#   Batch 1: add-plan, subtract-plan, multiply-plan, divide-plan
#   Batch 2: add-impl, subtract-impl, multiply-impl, divide-impl
#   ...
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

모든 남은 작업이 미완료 의존성에 의해 차단되면 실행이 중단되고 차단 사유가 출력된다.

## 로그

실행 로그는 `.ralph-logs/` 디렉토리에 저장된다:

```
.ralph-logs/
├── ralph-20260219-165209.log   # 세션 로그
├── add-plan.log                # 태스크별 로그 (병렬 실행 시)
├── subtract-plan.log
└── multiply-plan.log
```

```bash
# 로그 파일 목록
ralph --logs

# 특정 태스크 로그 보기
ralph --logs add-impl

# 실시간 로그 추적 (병렬 실행 중 모니터링)
ralph --logs --live add-impl
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
ralph --status            # 병렬 배치 구조 확인
ralph --run               # 실행 (Phase 1은 4개 동시, Phase 2~3은 순차)
```

이 PRD의 핵심 포인트:
- 각 모듈이 **별도 파일**을 수정하므로 병합 충돌 없이 병렬 실행 가능
- Phase와 의존성을 **명시적으로 기술**하여 plan generator가 정확한 `dependsOn`을 생성
- `"병렬 실행 가능"` 힌트를 PRD에 포함하여 병렬 구조 유도

## 보안

커밋 시 다음 패턴의 파일은 자동으로 제외된다:

`.env`, `.env.*`, `*.pem`, `*.key`, `*.p12`, `*.pfx`, `credentials.json`, `service-account*.json`, `.secret*`, `*.secrets`, `id_rsa`, `id_ed25519`

제외된 민감 파일이 감지되면 경고 메시지가 출력된다.
