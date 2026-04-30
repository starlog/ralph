# 스크립트 가이드

[English](SCRIPTS.en.md) | **한국어**

저장소 루트에 있는 스크립트 파일들의 목적과 사용법을 정리한 문서입니다. 각 스크립트는 POSIX 셸용(`*.sh`)과 PowerShell용(`*.ps1`)이 한 쌍으로 제공되며, 두 변형은 동일한 기능을 합니다.

| 스크립트 | 용도 | 사용자 |
|---|---|---|
| [`install-binary.sh`](install-binary.sh) / [`install-binary.ps1`](install-binary.ps1) | GitHub Releases에서 미리 빌드된 바이너리 다운로드 + 설치 | **일반 사용자** |
| [`install.sh`](install.sh) / [`install.ps1`](install.ps1) | 소스에서 빌드하여 로컬에 설치 (.NET 8 SDK 필요) | **소스 빌드 사용자** |
| [`release-binary.sh`](release-binary.sh) / [`release-binary.ps1`](release-binary.ps1) | 모든 플랫폼용 바이너리 빌드 + 태그/Release 생성 | **메인테이너 전용** |
| [`clean-sample.sh`](clean-sample.sh) / [`clean-sample.ps1`](clean-sample.ps1) | `samples/` 디렉터리 정리 (`PRD.md`만 남김) | **개발자/테스터** |

---

## install-binary.sh / install-binary.ps1

`.NET SDK` 없이 GitHub Releases에 올라온 self-contained 바이너리를 받아 설치합니다. 가장 권장되는 설치 방법입니다.

### 사용법 — POSIX (macOS / Linux / WSL / Git Bash)

```bash
# 가장 간단한 방법 (curl 파이프)
curl -fsSL https://raw.githubusercontent.com/starlog/ralph/main/install-binary.sh | bash

# 설치 디렉터리 지정
curl -fsSL https://raw.githubusercontent.com/starlog/ralph/main/install-binary.sh | bash -s -- --dir ~/.local/bin

# 특정 버전 지정
curl -fsSL https://raw.githubusercontent.com/starlog/ralph/main/install-binary.sh | bash -s -- --version v1.22

# 로컬 클론 후 직접 실행
./install-binary.sh --dir ~/bin --quiet
```

### 사용법 — Windows (PowerShell)

```powershell
# 가장 간단한 방법 (iwr 파이프)
iwr -useb https://raw.githubusercontent.com/starlog/ralph/main/install-binary.ps1 | iex

# 직접 실행 (옵션 지정)
.\install-binary.ps1 -Version v1.22 -Dir "$env:USERPROFILE\bin"
```

### 옵션

| 옵션 | 기본값 | 설명 |
|---|---|---|
| `--version` / `-Version` | latest | 설치할 release 태그 (예: `v1.22`) |
| `--dir` / `-Dir` | `$HOME/.local/bin` | 설치 디렉터리 |
| `--quiet` / `-Quiet` | off | 상세 로그 줄이기 |

### 환경변수

- `RALPH_REPO` — 소스 저장소 override (기본: `starlog/ralph`)

### 동작 흐름

1. OS / 아키텍처 자동 감지 (`linux-x64`, `osx-arm64`, `win-x64` 등)
2. 미지정 시 GitHub API로 최신 release 태그 조회
3. 압축 파일 다운로드 (POSIX: `tar.gz`, Windows: `zip`)
4. `SHA256SUMS.txt`가 함께 올라와 있으면 체크섬 검증
5. 압축 해제 후 설치 디렉터리에 복사 (POSIX는 `chmod +x`)
6. `PATH`에 없으면 추가 가이드 출력

---

## install.sh / install.ps1

소스 트리에서 직접 빌드하여 로컬에 설치합니다. `.NET 8 SDK`가 필요합니다.

### 사용법

```bash
# macOS / Linux
git clone https://github.com/starlog/ralph.git
cd ralph
./install.sh

# Windows (PowerShell)
git clone https://github.com/starlog/ralph.git
cd ralph
.\install.ps1
```

### 동작 흐름

1. OS / 아키텍처를 감지하여 RID 결정 (`linux-x64`, `osx-arm64`, `win-x64`, `win-arm64`)
2. `dotnet --version`으로 .NET SDK 존재 확인
3. `dotnet publish -c Release -r <RID>`로 self-contained 바이너리 생성
4. 설치 디렉터리 입력받기 (기본: `$HOME/bin`)
5. 디렉터리가 없으면 생성 여부 확인
6. 바이너리 복사 + 실행권한 부여 (POSIX)
7. `PATH`에 없으면 rc 파일(`.zshrc` / `.bashrc` / `.bash_profile`)에 추가할지 묻기 (POSIX) 또는 user PATH에 등록 (Windows)

### `install-binary.sh`와의 차이

| 항목 | `install.sh` | `install-binary.sh` |
|---|---|---|
| .NET SDK | **필요** | 불필요 |
| 소스 코드 | 필요 (clone) | 불필요 |
| 설치 시간 | 빌드 시간 포함 | 다운로드만 |
| 사용 시점 | 직접 수정 / 미배포 변경 검증 | 일반 사용 |

---

## release-binary.sh / release-binary.ps1

**메인테이너 전용 스크립트.** 모든 플랫폼용 바이너리를 빌드하고, git 태그를 만들어 push한 뒤 GitHub Release를 발행합니다.

### 사용법

```bash
# 자동 버전 bump (최신 태그부터 커밋 메시지를 분석)
./release-binary.sh

# 수동 버전 지정
./release-binary.sh --version v1.3

# +0.1 (major) 또는 +0.01 (minor) 강제
./release-binary.sh --bump major
./release-binary.sh --bump minor

# 빌드 + 패키징만 (태그/Release 미생성)
./release-binary.sh --dry-run

# 기존 dist/ 재사용
./release-binary.sh --skip-build
```

PowerShell:

```powershell
.\release-binary.ps1                 # 자동 bump
.\release-binary.ps1 -Bump major     # +0.1 강제
.\release-binary.ps1 -Version v1.3   # 명시적 버전
.\release-binary.ps1 -DryRun         # 빌드만
.\release-binary.ps1 -SkipBuild      # dist/ 재사용
.\release-binary.ps1 -NoTag          # 태그/푸시 건너뛰기
```

### 자동 bump 규칙

최신 `v*` 태그 이후의 커밋 메시지를 검사하여 다음 버전을 결정합니다.

- **+0.1 (major)** — `기능추가`, `기능개선`, `리팩토링`, `feat`, `BREAKING` 마커가 포함된 커밋이 하나라도 있으면
- **+0.01 (minor)** — 그 외 (docs / chore / fix만 있는 경우)

### 주요 옵션

| 옵션 | 설명 |
|---|---|
| `--version <tag>` | 명시적 release 태그 (자동 bump 무시) |
| `--bump major\|minor` | 자동 분석 결과 무시하고 강제 |
| `--notes <file>` | 자동 생성 대신 release notes 파일 사용 |
| `--draft` | Draft release로 발행 |
| `--prerelease` | Pre-release 표시 |
| `--no-tag` | git 태그 생성/푸시 건너뛰기 (이미 존재 가정) |
| `--no-push` | 태그를 로컬에서만 만들고 푸시 안 함 |
| `--allow-dirty` | 작업 트리가 dirty여도 태그 허용 |
| `--skip-build` | 기존 `dist/` 재사용 |
| `--dry-run` | 빌드 + 패키징만, 태그/Release 미생성 |

### 의존 도구

- `dotnet` — 빌드
- `git` — 태그 생성/푸시
- `gh` — GitHub Release 발행 (`--dry-run`에서는 불필요)
- `tar` — 압축

### 빌드 대상 플랫폼

`win-x64`, `osx-x64`, `osx-arm64`, `linux-x64`, `linux-arm64` (release 워크플로(`.github/workflows/release.yml`)와 동일).

### 환경변수

- `RALPH_REPO` — 대상 저장소 override (기본: `starlog/ralph`)

> **주의**: PowerShell 버전은 Windows 콘솔 `cp949`에서 git이 한국어 커밋 요약을 stdout에 쓸 때 죽지 않도록 콘솔 인코딩을 UTF-8로 강제합니다.

---

## clean-sample.sh / clean-sample.ps1

`samples/` 디렉터리에서 `PRD.md`를 제외한 모든 파일/디렉터리를 삭제합니다. Ralph 동작을 검증할 때 생성된 산출물을 한 번에 정리하는 용도입니다.

### 사용법

```bash
# POSIX
./clean-sample.sh

# PowerShell
.\clean-sample.ps1
```

### 동작 흐름

1. `samples/` 디렉터리 존재 확인
2. `PRD.md`를 제외한 모든 항목을 재귀적으로 삭제
3. 완료 메시지 출력

> **주의**: 삭제는 비가역입니다. `samples/` 안에 보관해둔 결과물이 있으면 먼저 다른 곳으로 옮긴 뒤 실행하세요.
