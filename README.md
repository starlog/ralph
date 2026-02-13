# Ralph

PRD(Product Requirements Document) 기반 작업 계획을 생성하고, Claude Code를 통해 순차적으로 자동 실행하는 CLI 태스크 오케스트레이터.

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

## 설치

### 필수 의존성

| 도구 | 설치 |
|---|---|
| jq | `brew install jq` |
| claude | [Claude Code](https://claude.ai/code) 설치 |
| git | 기본 포함 |

### 설치 방법

```bash
git clone <repository-url>
cd ralph
./install.sh
```

`install.sh`는 `ralph.sh`와 `ralph-schema.json`을 `~/bin`에 복사하고 PATH를 설정한다.

설치 후 터미널을 재시작하거나:

```bash
source ~/.zshrc   # 또는 ~/.bashrc
```

## 사용법

### 기본 워크플로우

```bash
# 1. PRD에서 작업 계획 생성
ralph.sh --plan docs/PRD.md

# 2. 생성된 작업 확인
ralph.sh --list

# 3. 실행 미리보기 (실제 변경 없음)
ralph.sh --dry-run

# 4. 전체 작업 자동 실행
ralph.sh --run
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
ralph.sh --run my-project-tasks.json
```

### 대화형 모드

`--interactive`로 실행하면 각 작업마다 선택지가 표시된다:

- `y` — 실행
- `n` — 건너뛰기
- `p` — 프롬프트 미리보기
- `s` — 건너뛰기
- `q` — 종료

### 환경 변수

| 변수 | 기본값 | 설명 |
|---|---|---|
| `MAX_RETRIES` | 2 | Claude Code 실행 실패 시 재시도 횟수 |
| `RETRY_DELAY` | 5 | 재시도 간 대기 시간 (초) |
| `CLAUDE_CODE_MAX_OUTPUT_TOKENS` | 65536 | plan 생성 시 최대 토큰 수 |

```bash
MAX_RETRIES=3 RETRY_DELAY=10 ralph.sh --run
```

## tasks.json 구조

`ralph.sh --plan`으로 자동 생성되거나 직접 작성할 수 있다. 스키마는 `ralph-schema.json`에 정의되어 있다.

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

### 최상위 속성

| 속성 | 필수 | 타입 | 설명 |
|---|---|---|---|
| `projectName` | | string | 프로젝트 이름 |
| `version` | | string | 버전 (예: `"1.0.0"`) |
| `workflow` | | object | 작업 완료 후 동작 설정 |
| `apiSpecs` | | object | API 사양 참조 (프롬프트에서 활용) |
| `samplePages` | | object | 샘플 페이지 정의 (프롬프트에서 활용) |
| `tasks` | **필수** | array | 작업 배열 |

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
ralph.sh --logs

# 최신 로그 내용 보기
cat .ralph-logs/$(ls -t .ralph-logs/ | head -1)
```

## 보안

커밋 시 다음 패턴의 파일은 자동으로 제외된다:

`.env`, `.env.*`, `*.pem`, `*.key`, `*.p12`, `*.pfx`, `credentials.json`, `service-account*.json`, `.secret*`, `*.secrets`, `id_rsa`, `id_ed25519`

제외된 민감 파일이 감지되면 경고 메시지가 출력된다.
