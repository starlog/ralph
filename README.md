# Ralph

PRD(Product Requirements Document) 기반 작업 계획을 생성하고, Claude Code를 통해 순차적으로 자동 실행하는 CLI 태스크 오케스트레이터.
Dotnet Core 8 Lts로 구현 (Windows, MacOS, Linux에서 다 사용해야 해서)

## 동작 원리

Ralph는 기능 단위로 **4단계 패턴**을 따른다:

```
plan → implementation → testing → commit
```

각 기능(feature)마다 위 4개의 태스크가 생성되며, 의존성 체인으로 연결되어 순서가 보장된다.

```
user-auth-plan ─→ user-auth-impl ─→ user-auth-test ─→ user-auth-commit
                                                              │
payment-plan ─→ payment-impl ─→ payment-test ─→ payment-commit
```

## 버전

| 버전 | 구현 | 플랫폼 |
|---|---|---|
| v0.1 | `ralph.sh` (Bash) | macOS, Linux |
| v0.2 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux |

## 설치

### .NET 8 버전 (v0.2, 권장)

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

### Bash 버전 (v0.1, macOS/Linux 전용)

#### 필수 의존성

| 도구 | 설치 |
|---|---|
| jq | `brew install jq` |
| claude | [Claude Code](https://claude.ai/code) 설치 |
| git | 기본 포함 |

#### 설치 방법

```bash
./install.sh
```

`install.sh`는 `ralph.sh`와 `ralph-schema.json`을 `~/bin`에 복사하고 PATH를 설정한다.

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
| `--run [파일]` | 모든 pending 작업 자동 실행. 파일 미지정 시 `tasks.json` 사용 |
| `--dry-run` | 실행 시뮬레이션 (tasks.json 변경 없음) |
| `--task <id>` | 특정 작업 하나만 실행 |
| `--interactive` | 대화형 모드 — 각 작업마다 확인 후 실행 |
| `--list`, `-l` | pending 작업 목록 출력 |
| `--prompts`, `-p` | 모든 작업의 Claude 프롬프트 출력 |
| `--status`, `-s` | 진행 상황 대시보드 |
| `--reset`, `-r` | 모든 작업을 pending으로 초기화 |
| `--logs` | 최근 로그 파일 목록 |
| `--help`, `-h` | 도움말 |

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
| `CLAUDE_CODE_MAX_OUTPUT_TOKENS` | 65536 | plan 생성 시 최대 토큰 수 |

```bash
# Linux/macOS
MAX_RETRIES=3 RETRY_DELAY=10 ralph --run

# Windows (PowerShell)
$env:MAX_RETRIES=3; $env:RETRY_DELAY=10; ralph --run
```

## 프로젝트 구조

```
ralph/
├── Ralph/                      # .NET 8 프로젝트 (v0.2)
│   ├── Ralph.csproj            # 프로젝트 설정 (단일 파일 배포)
│   ├── Program.cs              # CLI 진입점 및 명령어 처리
│   ├── Models/
│   │   ├── TasksFile.cs        # tasks.json 모델 (TaskItem, SubTask 등)
│   │   └── RalphJsonContext.cs # JSON 소스 생성기 (IL 트리밍 호환)
│   └── Services/
│       ├── ClaudeService.cs    # Claude Code 프로세스 실행 및 스트리밍
│       ├── TaskManager.cs      # tasks.json 로드/저장/쿼리
│       ├── GitService.cs       # Git 커밋 자동화
│       ├── PlanGenerator.cs    # PRD → tasks.json 생성
│       └── RalphLogger.cs      # 파일 로깅
├── ralph.sh                    # Bash 버전 (v0.1)
├── ralph-schema.json           # tasks.json JSON Schema
├── install.sh                  # Bash 버전 설치 스크립트
├── CLAUDE.md                   # Claude Code 가이드
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
| `dependsOn` | | string[] | 선행 작업 ID 배열. 해당 작업이 모두 완료되어야 실행 가능 |
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

## 의존성 관리

`dependsOn`으로 작업 간 실행 순서를 제어한다. 선행 작업이 모두 `done: true`가 되어야 해당 작업이 실행된다.

```json
{
  "tasks": [
    { "id": "auth-plan", "title": "인증 설계", "done": false },
    { "id": "auth-impl", "title": "인증 구현", "done": false, "dependsOn": ["auth-plan"] },
    { "id": "auth-test", "title": "인증 테스트", "done": false, "dependsOn": ["auth-impl"] },
    { "id": "auth-commit", "title": "인증 커밋", "done": false, "dependsOn": ["auth-test"] }
  ]
}
```

모든 남은 작업이 미완료 의존성에 의해 차단되면 실행이 중단되고 차단 사유가 출력된다.

## 로그

실행 로그는 `.ralph-logs/` 디렉토리에 저장된다:

```
.ralph-logs/ralph-20260213-143022.log
```

```bash
# 최근 로그 확인
ralph --logs
```

## 보안

커밋 시 다음 패턴의 파일은 자동으로 제외된다:

`.env`, `.env.*`, `*.pem`, `*.key`, `*.p12`, `*.pfx`, `credentials.json`, `service-account*.json`, `.secret*`, `*.secrets`, `id_rsa`, `id_ed25519`

제외된 민감 파일이 감지되면 경고 메시지가 출력된다.
