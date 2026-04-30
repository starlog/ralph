---
description: 최신 구현을 반영하도록 CLAUDE.md, README.md, README.en.md, TECHNICAL.md, TECHNICAL.en.md 문서를 업데이트합니다.
---

저장소의 핵심 문서를 현재 코드베이스의 최신 상태와 일치시키세요.

## 수행 작업

1. **현재 구현 파악** — 다음을 확인:
   - `git status` / `git diff` 로 아직 커밋되지 않은 변경 사항
   - `git log --oneline -20` 로 최근 커밋 흐름
   - `Ralph/Services/`, `Ralph/Commands/`, `Ralph/Models/`, `ralph-schema.json` 의 실제 구조 (특히 새로 추가/삭제된 파일)
   - `Program.cs` 와 `CommandDispatcher.cs` 의 서브커맨드 목록
   - 최신 버전 (`Ralph/Ralph.csproj` 의 `<Version>` 또는 `--version`)

2. **다음 5개 문서를 업데이트**:
   - `CLAUDE.md` — Claude Code용 프로젝트 가이드 (아키텍처, 서비스 표, 실행 흐름, 명령, 환경 변수, 워크플로우 설정, 컨벤션)
   - `README.md` — 한국어 사용자용 README (vibe-coder 친화적, 간단한 사용 예시 위주)
   - `README.en.md` — 영문 README (README.md 의 영문 버전)
   - `TECHNICAL.md` — 한국어 기술 상세 문서 (서비스/모델/실행 흐름의 깊은 설명)
   - `TECHNICAL.en.md` — TECHNICAL.md 의 영문 버전

3. **업데이트 원칙**:
   - 실제 코드와 일치하지 않는 설명 (사라진 서비스, 이름이 바뀐 명령, 더 이상 존재하지 않는 필드 등) 을 모두 수정
   - 새로 추가된 서비스/명령/환경 변수/워크플로우 설정을 빠짐없이 반영
   - 한/영 문서 (`README.md` ↔ `README.en.md`, `TECHNICAL.md` ↔ `TECHNICAL.en.md`) 의 내용·구조·예시가 서로 어긋나지 않도록 동기화
   - 버전 번호가 본문에 인용되어 있다면 최신 값으로 갱신
   - 추측하지 말 것: 코드에서 직접 확인되지 않는 동작은 적지 않거나 실제 코드 기준으로 다시 작성
   - 불필요한 새 섹션/예시를 만들지 말고 기존 구조를 유지한 채 내용만 갱신

4. **검증**:
   - 변경 후 각 문서에 언급된 파일 경로 (`Ralph/Services/X.cs` 등) 가 실제로 존재하는지 빠르게 확인
   - 명령 예시 (`ralph --foo`) 의 플래그가 `ArgParser` / `CommandDispatcher` 에 실제 존재하는지 확인

5. 작업이 끝나면 어떤 문서에서 무엇이 바뀌었는지 1-2 줄로 요약해서 보고. 커밋/푸시는 사용자가 별도로 요청할 때까지 하지 말 것.
