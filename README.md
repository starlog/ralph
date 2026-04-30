# Ralph

[English](README.en.md) | **한국어**

> 큰 일거리 한 장 → 여러 명의 Claude가 동시에 작업 → 자동으로 합쳐서 완성.
> .NET 8 기반 단일 바이너리. Windows / macOS / Linux. 현재 버전: **v1.22**.

---

## Ralph가 뭔가요?

요즘 AI한테 코딩을 시키는 게 일상이 됐죠. 그런데 한 번에 큰 기능을 시키면 — "로그인 만들고 결제 붙이고 알림도 추가해 줘" — Claude는 한 줄로 길게 한 가지씩 합니다. 시간이 오래 걸리고 중간에 흐름이 흐트러지기 쉽습니다.

**Ralph는 그 큰 일거리를 받아서 알아서 잘게 쪼개고, 독립적인 부분은 여러 명의 Claude를 동시에 돌려서 끝낸 다음, 결과를 한 줄로 합쳐 줍니다.**

```
   PRD.md  (한 페이지 기획서)
       ↓  ralph --plan
   tasks.json  (작은 작업 24개로 분해)
       ↓  ralph --run
   [로그인]  [결제]  [알림]  [설정]      ← Claude 4명 동시 실행
       ↓  자동 머지 + 빌드 검증
   완성된 코드
```

### 다른 AI 코딩 도구와 뭐가 다른가요

- **여러 명을 동시에 돌립니다.** 보통 AI 도구는 한 번에 한 가지만 합니다. Ralph는 git의 worktree 기능으로 작업 공간을 격리한 뒤 독립적인 부분을 동시에 진행시킵니다 — 4개 기능이면 4배 빠릅니다.
- **빌드/테스트로 자기 검증합니다.** "다 했어요"라는 Claude의 말이 아니라 `dotnet build` / `pytest` 같은 명령의 종료 코드로 진짜 됐는지 확인합니다. 실패하면 자동으로 한 번 더 시도합니다.
- **돈을 정해놓을 수 있습니다.** `--budget-usd 5` 처럼 상한을 걸면 그 이상 쓰지 않습니다.
- **중간에 끊겨도 이어집니다.** 컴퓨터를 껐다 켜도 정확히 멈춘 자리부터 다시 진행합니다.
- **단일 실행파일.** 다른 거 안 깔아도 됩니다 (claude, git만 있으면 됩니다).

상세한 기능 / 설정 / 동작 원리 / 트러블슈팅은 **[TECHNICAL.md](TECHNICAL.md)** 에 있습니다.

---

## ⚠️ 보안 주의 — 먼저 읽어 주세요

Ralph는 호스트 머신에서 Claude Code를 `--dangerously-skip-permissions`로 직접 실행합니다. 즉, **Claude가 여러분의 컴퓨터에 있는 파일을 자유롭게 읽고 쓸 수 있습니다** — `.env`, SSH 키, AWS 자격증명 등이 노출될 수 있습니다.

본인이 작성한/이해하는 PRD라면 일반 개발 환경에서 써도 괜찮지만, **남이 준 PRD나 `tasks.json`** 은 반드시 별도 사용자 계정 / VM / 컨테이너에서 돌리세요.

---

## 필요한 것

| 도구 | 설명 |
|---|---|
| [Claude Code](https://claude.ai/code) | Claude Code CLI. Ralph가 내부에서 호출합니다. |
| [git](https://git-scm.com/) | 병렬 실행에 worktree를 씁니다. (2.10+ 권장) |

---

## 설치

세 가지 방법 중 하나 고르면 됩니다.

### 방법 1 — 미리 빌드된 바이너리 받기 (가장 쉬움)

[GitHub Releases](https://github.com/starlog/ralph/releases) 에서 본인 OS에 맞는 파일을 받습니다. .NET 설치 필요 없음.

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

### 방법 2 — 패키지 매니저

```bash
# macOS / Linux — Homebrew
brew tap starlog/ralph https://github.com/starlog/ralph
brew install ralph

# Windows — Scoop
scoop install https://raw.githubusercontent.com/starlog/ralph/main/scoop/ralph.json
```

### 방법 3 — 소스에서 빌드 (.NET 8 SDK 필요)

```bash
git clone https://github.com/starlog/ralph.git
cd ralph

# macOS / Linux
./install.sh

# Windows (PowerShell)
.\install.ps1
```

설치 후 확인:

```bash
ralph --version
```

---

## 사용법 (5분 시작)

### 1단계 — 기획서(PRD) 한 장 쓰기

`PRD.md`에 만들고 싶은 걸 자연어로 적습니다. 독립된 기능을 phase로 나누면 Ralph가 알아서 병렬로 만듭니다.

```markdown
# 계산기 앱

## Phase 1 — 연산 모듈 (서로 독립)
- `add.py`에 add(a, b)
- `subtract.py`에 subtract(a, b)
- `multiply.py`에 multiply(a, b)

## Phase 2 — CLI (Phase 1 이후)
- `main.py`에서 위 모듈을 import해 CLI 노출
```

`samples/PRD.md`에 더 자세한 예시가 있습니다.

### 2단계 — 작업으로 변환

```bash
ralph --plan PRD.md
```

PRD를 분석해서 `tasks.json`을 만듭니다 (24개 정도의 작은 작업으로 쪼개짐).

### 3단계 — 어떻게 진행될지 미리 보기

```bash
ralph --graph    # 의존성 그래프
ralph --list     # 작업 목록
ralph --dry-run  # 실제로 실행하진 않고 시뮬레이션
```

### 4단계 — 실행

```bash
ralph --run
```

Phase 1의 독립적인 모듈은 동시에, Phase 2는 그 뒤에 순차적으로 진행됩니다. 콘솔에 라이브 진행 표가 뜨고 끝나면 모두 머지됩니다.

### 자주 쓰는 옵션

```bash
ralph --run --budget-usd 5.00      # 5달러 넘으면 새 작업 시작 안 함
ralph --run --max-parallel 3       # 한 번에 최대 3개만 동시에
ralph --run --task-timeout 30m     # 한 호출이 30분 넘으면 강제 종료
ralph --status                     # 진행 상황 보기
ralph --cost                       # 누적 비용 / 토큰
ralph --rollback                   # 직전 상태로 되돌리기 (--plan 또는 --run 직전)
```

전체 명령어 / 옵션 / 환경변수 목록은 **[TECHNICAL.md](TECHNICAL.md)** 참고.

---

## 더 깊이 알고 싶다면

- **[TECHNICAL.md](TECHNICAL.md)** — 전체 명령어 / 옵션 / 환경변수, 병렬 실행 흐름, 검증 게이트, 충돌 전략, smoke test, tasks.json 스키마, workflow 설정, PRD 작성 가이드, 트러블슈팅, 설계 노트.
- **[SCRIPTS.md](SCRIPTS.md)** — 루트의 설치/릴리스/유틸리티 스크립트(`install*`, `release-binary*`, `clean-sample*`) 사용법.
- **[CLAUDE.md](CLAUDE.md)** — LLM 기여자를 위한 서비스 단위 아키텍처 맵.
- **[samples/PRD.md](samples/PRD.md)** — 병렬 실행에 최적화된 예시 PRD.

---

## 라이선스 / 기여

Pull Request / Issue 환영합니다. 빌드와 테스트:

```bash
dotnet build ralph.sln
dotnet test ralph.sln
```

자세한 개발 안내는 **[TECHNICAL.md](TECHNICAL.md#기여-및-개발)** 참고.
