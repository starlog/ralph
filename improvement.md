# Ralph 개선 계획

> starlog/ralph (v0.7) 기준 개선 항목. 우선순위 순으로 정렬되어 있으며, 각 항목은 독립적으로 구현 가능하다.

## 우선순위 기준

- **P0 (Critical)** — Ralph가 정통 Ralph 패턴을 표방한다면 반드시 있어야 하는 기능. 없으면 long-running AFK 사용 시 품질이 무너진다.
- **P1 (Important)** — 병렬 실행이라는 차별화 기능을 안전하고 경제적으로 쓰기 위해 필요한 것들.
- **P2 (Nice to have)** — 사용성, 채택률, polish 관련.

---

## P0-1. Learning Loop (AGENTS.md 자동 업데이트)

### 문제

원본 Ralph의 핵심 통찰은 단순 loop가 아니라 **iteration 간 학습 누적**이다. Geoffrey Huntley 패턴은 각 iteration이 끝나면 발견된 patterns, gotchas, conventions를 `AGENTS.md`에 추가해서 다음 iteration이 자동으로 읽도록 한다. 현재 starlog/ralph는 task 완료 시 commit만 하고 학습이 어디에도 누적되지 않는다.

이게 왜 문제냐면, Phase 2의 `main-impl`이 Phase 1의 4개 모듈(`add`, `subtract`, `multiply`, `divide`) 구현 중 발견된 코딩 컨벤션, 함정, 결정사항을 모르고 시작한다. 같은 실수를 반복하거나 일관성 없는 코드가 나온다.

### 제안

각 task 완료 후 별도 prompt로 "이번에 배운 것"을 추출하고 `AGENTS.md`에 누적한다.

### 구현 노트

```csharp
// Services/LearningExtractor.cs (신규)
public class LearningExtractor
{
    public async Task<string> ExtractLearnings(TaskItem task, string taskOutput)
    {
        var prompt = $"""
        방금 완료한 task: {task.Title}
        수정한 파일: {string.Join(", ", task.ModifiedFiles ?? [])}

        다음 형식으로 향후 task가 알아야 할 사항을 5개 이하로 추출:
        - Pattern: <코드베이스에서 사용하는 X for Y>
        - Gotcha: <조심해야 할 점>
        - Convention: <지켜야 할 컨벤션>
        해당 사항이 없으면 "NONE"만 출력.
        """;
        return await _claudeService.RunOneshot(prompt);
    }
}
```

`tasks.json`의 `workflow.onTaskComplete`에 옵션 추가:

```json
{
  "workflow": {
    "onTaskComplete": {
      "commitChanges": true,
      "updateAgentsMd": true,
      "agentsMdPath": "AGENTS.md"
    }
  }
}
```

### Worktree 환경에서의 주의점

병렬 task A, B, C가 각자 worktree에서 돌면 AGENTS.md 업데이트가 충돌한다. 해결책:

1. 각 worktree는 자신의 learning을 `.ralph/learnings/{taskId}.md`에 임시 저장
2. Batch 종료 후 main 브랜치에서 모든 learning을 모아 AGENTS.md에 한 번에 append
3. Append 시 중복 제거 (Claude로 dedup하거나 단순 string match)

### 예상 영향

- 같은 batch 내 task끼리는 학습 공유 불가 (worktree 격리 때문). 다음 batch부터 효과 발생.
- Token 비용: task당 평균 +500~1000 token (extraction prompt).

---

## P0-2. Verification Gate (실제 완료 검증)

### 문제

현재 `MAX_RETRIES=2`는 Claude Code 프로세스 실패 시에만 재시도한다. 즉 **Claude가 말을 끝내면 task는 done으로 표시**된다. 실제로 코드가 컴파일되는지, test가 통과하는지는 검증하지 않는다.

정통 Ralph 구현체들은 verification command (`npm test`, `dotnet build`, `pytest`)를 돌려서 exit code 0일 때만 task를 done으로 본다. 실패 시 같은 task를 재실행하되, 실패 출력을 다음 iteration의 prompt에 포함한다.

`category: "testing"`을 별도 task로 두는 현재 방식은 약하다. 왜냐하면 testing task가 실패해도 implementation task는 이미 done이라 git history에 깨진 코드가 commit되어 있다.

### 제안

Task 객체에 verification 필드 추가:

```json
{
  "id": "add-impl",
  "title": "덧셈 모듈 구현",
  "verification": {
    "command": "pytest tests/test_add.py -x",
    "maxRetries": 3,
    "timeoutSeconds": 120
  }
}
```

실행 흐름:

```
1. Claude Code 실행 (구현)
2. verification.command 실행
3. exit 0이면 → done, commit
4. exit != 0이면 → stderr/stdout을 다음 iteration prompt에 포함하여 재실행
5. maxRetries 초과 시 → task 실패, 의존하는 task들 blocked
```

### 구현 노트

- `Services/VerificationRunner.cs` 신규
- `ClaudeService.cs`의 task execution loop를 inner-loop 구조로 변경
- 재실행 시 prompt에 다음 추가:
  ```
  이전 시도가 다음 검증에서 실패했습니다:
  Command: {verification.command}
  Exit code: {exitCode}
  Output:
  {stdout + stderr}

  실패 원인을 분석하고 수정하세요.
  ```

### `category: "testing"` task와의 관계

- 기존 4단계 패턴은 유지하되, 각 단계에 자체 verification을 붙일 수 있게 한다.
- `plan` task → output file 존재 검증
- `impl` task → build/compile 검증
- `test` task → test 실행 검증
- `commit` task → verification 불필요 (또는 `git log -1` 같은 sanity check)

### Plan generator 수정

`PlanGenerator.cs`가 PRD에서 verification command를 추론해 자동 생성하도록. PRD에 명시되지 않으면:

- Python: `python -m py_compile {modifiedFiles}` (impl), `pytest {testFile}` (test)
- TypeScript: `tsc --noEmit` (impl), `vitest run {testFile}` (test)
- Rust: `cargo build` (impl), `cargo test` (test)

언어 감지는 `package.json`, `pyproject.toml`, `Cargo.toml` 등 manifest 파일로.

---

## P0-3. Cost / Token Budget Control

### 문제

`maxConcurrent: 3` × `4 phases` × N features면 Claude Max의 weekly limit를 순식간에 소진한다. AFK로 돌려놓고 자다 일어났더니 limit 초과로 멈춰 있고 진행도 안 된 상태가 가능하다.

현재 token/cost 추적이 없어서:

1. 실행 후 얼마나 썼는지 모른다.
2. 임계값 도달 시 graceful pause가 없다.
3. PRD plan 단계에서 비용 예측이 불가능하다.

### 제안

#### 3-1. 누적 추적

`.ralph-logs/cost.json` 파일에 누적 기록:

```json
{
  "session": "20260219-165209",
  "totalTokensInput": 1250000,
  "totalTokensOutput": 380000,
  "estimatedCostUsd": 12.45,
  "byTask": {
    "add-impl": { "inputTokens": 8200, "outputTokens": 2100, "durationSec": 47 },
    "subtract-impl": { "inputTokens": 7900, "outputTokens": 2050, "durationSec": 44 }
  }
}
```

Claude Code의 `--output-format json` 또는 stream-json을 파싱해서 token 정보를 추출.

#### 3-2. Budget 옵션

```bash
ralph --run --budget-tokens 5000000
ralph --run --budget-usd 50
ralph --run --budget-time 6h
```

임계값의 80% 도달 시 경고, 100% 도달 시 현재 batch 완료 후 graceful pause.

#### 3-3. Pause / Resume

```bash
ralph --run --resume    # tasks.json의 done 상태에서 이어서 시작
```

Pause 시 진행 중이던 worktree는 중단점 표시 + 다음 실행 시 안내.

#### 3-4. 비용 예측 (선택)

`--plan` 단계에서 각 task의 prompt 길이 + output file 예상 크기로 token 예측. 정확하진 않지만 order-of-magnitude 추정으로 충분.

### 구현 노트

- `Services/CostTracker.cs` 신규
- Claude Code 실행 시 `--output-format stream-json` 사용해서 token 정보 capture
- `--budget-*` 플래그는 `Program.cs`에서 처리하고 `ParallelExecutor`에 전달

---

## P1-1. modifiedFiles 정확성 검증

### 문제

병렬 worktree merge의 안전성은 `modifiedFiles` 정확성에 100% 의존한다. PRD에서 plan generator가 추론한 `modifiedFiles`가 부정확하면:

- Task A의 `modifiedFiles: ["add.py"]`인데 실제로 `utils.py`도 수정
- Task B는 `utils.py`를 수정하는 게 명시되어 있음
- → A와 B가 병렬 실행되고, merge 시 utils.py에서 silent overwrite 발생 가능

이게 무서운 이유는 git이 conflict 마커를 안 띄울 수 있다는 것. 한쪽 변경이 사라져도 컴파일은 되고, test가 그 부분을 안 다루면 발견되지 않는다.

### 제안

Worktree 종료 시 검증:

```csharp
// WorktreeService.cs
public class WorktreeValidationResult
{
    public List<string> DeclaredFiles { get; set; }      // task.modifiedFiles
    public List<string> ActuallyChanged { get; set; }    // git diff --name-only
    public List<string> Undeclared { get; set; }         // ActuallyChanged - DeclaredFiles
    public List<string> NotChanged { get; set; }         // DeclaredFiles - ActuallyChanged
}
```

`Undeclared`가 비어있지 않으면:

1. **Strict mode** — task 실패 처리, merge 중단
2. **Warn mode (기본)** — 로그에 경고, merge 진행하되 다른 task와 겹치는 파일이 있으면 sequential fallback

### Plan generator 품질 신호

`Undeclared`가 누적되면 그 PRD의 plan generation 품질이 낮다는 신호. 다음 plan generation 시 prompt에 다음을 추가:

```
주의: 이 프로젝트의 이전 task에서 modifiedFiles에 누락이 자주 발견되었습니다.
실제로 수정될 모든 파일을 빠짐없이 명시하세요. 특히 다음 파일들이 자주 누락됩니다: [...]
```

### 구현 노트

- `WorktreeService.cs`의 merge 직전에 검증 추가
- `.ralph-logs/validation.json`에 누적 기록
- `--strict` 플래그로 strict/warn 모드 전환

---

## P1-2. Worktree 격리에서의 CLAUDE.md / AGENTS.md 동기화

### 문제

병렬 task 4개가 각자 worktree에서 도는데, task A가 CLAUDE.md를 업데이트하면 동시 진행 중인 task B, C, D는 그 변경을 못 본다 (이미 worktree 분기됨). 같은 batch 내 학습 공유가 불가능하다.

또한 main 브랜치에서 CLAUDE.md를 merge할 때 4개 worktree가 모두 같은 파일을 수정했다면 conflict가 거의 보장된다.

### 제안

#### 2-1. 같은 batch 내 학습 공유 포기 (현실적 선택)

병렬은 본질적으로 fork-join 모델이므로, 같은 batch 내 학습 공유는 불가능에 가깝다. 대신:

- 각 worktree는 자신의 learning을 `.ralph/learnings/{taskId}.md`로 작성
- Batch 종료 시 main에서 consolidation step 실행 → 모든 learning을 AGENTS.md에 통합
- 다음 batch가 시작될 때는 통합된 AGENTS.md를 갖고 시작

#### 2-2. Consolidation step

```csharp
// Services/PostBatchConsolidator.cs
public async Task Consolidate(List<TaskItem> completedBatch)
{
    var learnings = completedBatch
        .Select(t => File.ReadAllText($".ralph/learnings/{t.Id}.md"))
        .ToList();

    var prompt = $"""
    다음은 방금 완료된 {completedBatch.Count}개 task의 learning입니다.
    중복을 제거하고, 일관성 있게 정리하여 AGENTS.md에 추가할 형태로 출력하세요.

    {string.Join("\n---\n", learnings)}
    """;

    var consolidated = await _claudeService.RunOneshot(prompt);
    await File.AppendAllTextAsync("AGENTS.md", "\n\n" + consolidated);
    await GitService.Commit("[Ralph] Consolidate batch learnings");
}
```

#### 2-3. CLAUDE.md는 read-only로 보호

Task 실행 시 prompt에 명시:

```
CLAUDE.md는 읽기 전용입니다. 수정하지 마세요.
프로젝트 컨벤션 변경 사항은 task 출력에 "## CONVENTION_UPDATE" 섹션으로 보고하세요.
```

→ Ralph가 CONVENTION_UPDATE를 감지하면 consolidation step에서 처리.

---

## P1-3. Sandbox 실행 옵션

### 문제

병렬로 4개 Claude Code가 동시에 host 파일시스템을 만진다. Worktree로 코드 격리는 되지만 다음은 격리되지 않는다:

- `~/.aws/credentials`
- `~/.ssh/`
- `~/.config/gcloud/`
- 환경 변수 (API keys 등)
- 시스템 패키지 설치 (`sudo apt install ...` 같은 명령 실행 시)

신뢰하지 않는 PRD를 돌릴 때 위험하다. PageAI나 다른 최신 구현체는 Docker sandbox 안에서 Claude Code를 `--dangerously-skip-permissions`로 실행한다.

### 제안

선택적 sandbox 모드:

```bash
ralph --run --sandbox docker
ralph --run --sandbox podman
ralph --run --sandbox none    # 기본값, 현재 동작
```

#### Docker 구현 개요

```bash
# 각 worktree마다 컨테이너 실행
docker run --rm \
  -v {worktreePath}:/workspace \
  -v ~/.config/claude:/root/.config/claude:ro \
  -w /workspace \
  --network=bridge \
  -e ANTHROPIC_API_KEY \
  ralph-runner:latest \
  claude --dangerously-skip-permissions -p "{prompt}"
```

`ralph-runner` 이미지는 별도로 빌드/배포. 기본 도구(node, python, dotnet, git)와 Claude Code CLI만 포함.

#### 점진적 도입

1차 release에서는:
- Docker가 설치되어 있으면 자동 감지 + `--sandbox docker` 권장 메시지
- 실제 sandbox 실행은 v0.8 이후로

#### README 경고 (즉시 적용 가능)

```markdown
## ⚠️ 보안 주의

Ralph는 Claude Code를 host에서 직접 실행한다. 신뢰하지 않는 PRD나
외부 출처의 tasks.json은 격리된 환경(별도 사용자 계정, VM, container)에서
실행할 것. 특히 다음 정보가 노출될 수 있다:
- ~/.ssh, ~/.aws, ~/.config 등의 자격증명
- 환경 변수에 저장된 API key
- 호스트의 모든 파일에 대한 read 권한
```

---

## P1-4. Crash Recovery / Resume

### 문제

`--worktree-cleanup` 명령이 존재한다는 것은 잔존 worktree 문제가 알려진 이슈라는 뜻. 장시간 AFK 실행 중 다음 시나리오들에 어떻게 대응하는지 불명확:

- Ralph 프로세스 자체 crash
- 시스템 재부팅
- 진행 중이던 worktree에서 Claude Code가 응답 없음 (hang)
- `tasks.json` 쓰기 도중 중단 (corruption 가능성)

### 제안

#### 4-1. Atomic state writes

`tasks.json`을 직접 덮어쓰지 말고:

```
1. tasks.json.tmp에 쓰기
2. fsync
3. mv tasks.json.tmp tasks.json (atomic on POSIX)
```

Windows에서는 `MoveFileEx` with `MOVEFILE_REPLACE_EXISTING + MOVEFILE_WRITE_THROUGH`.

#### 4-2. In-progress 상태 명시

현재 task의 `done` 필드는 `true`/`false` 두 가지인데, `in_progress`/`failed`도 추가:

```json
{
  "id": "add-impl",
  "status": "in_progress",
  "startedAt": "2026-04-28T10:23:11Z",
  "worktreePath": ".ralph-worktrees/add-impl",
  "lastHeartbeat": "2026-04-28T10:24:45Z"
}
```

`done: boolean`은 backward compat용으로 유지하되 deprecated.

#### 4-3. Resume 명령

```bash
ralph --run --resume
```

동작:

1. `tasks.json` 로드, `in_progress` 상태인 task 발견
2. 마지막 heartbeat이 N분 이상 오래되었으면 stale로 판단
3. Stale task의 worktree 상태 확인:
   - 변경사항 있고 commit되지 않음 → 사용자에게 어떻게 할지 묻기 (discard / retry / manual review)
   - 변경사항 없음 → worktree 삭제 후 pending으로 되돌리기
4. Pending task부터 정상 실행

#### 4-4. Heartbeat

Claude Code 실행 중 30초마다 `lastHeartbeat` 업데이트. Hang 감지 가능 (예: 10분 이상 heartbeat 없으면 강제 종료 + retry).

---

## P2-1. Pre-built Binaries

### 문제

현재 진입장벽이 높다. 사용자가:

1. .NET 8 SDK 설치
2. 레포 clone
3. `dotnet publish` 4가지 RID 중 자기 플랫폼 선택
4. 출력 경로에서 binary 찾아 PATH 설정

이것만 해도 잠재 사용자의 절반은 떨어져 나간다. 별 0개의 한 원인일 수 있다.

### 제안

GitHub Actions로 release 자동화:

```yaml
# .github/workflows/release.yml
name: Release
on:
  push:
    tags: ['v*']
jobs:
  build:
    strategy:
      matrix:
        include:
          - { os: ubuntu-latest, rid: linux-x64,   ext: '' }
          - { os: ubuntu-latest, rid: linux-arm64, ext: '' }
          - { os: macos-latest,  rid: osx-x64,     ext: '' }
          - { os: macos-latest,  rid: osx-arm64,   ext: '' }
          - { os: windows-latest, rid: win-x64,    ext: '.exe' }
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '8.0.x' }
      - run: dotnet publish Ralph -c Release -r ${{ matrix.rid }} -o publish
      - run: |
          cd publish
          tar czf ../ralph-${{ matrix.rid }}.tar.gz ralph${{ matrix.ext }}
      - uses: softprops/action-gh-release@v2
        with:
          files: ralph-${{ matrix.rid }}.tar.gz
```

추가로:

```bash
# install.sh를 universal installer로 재작성
curl -fsSL https://raw.githubusercontent.com/starlog/ralph/main/install.sh | sh
```

OS/arch 감지 → 적절한 binary download → `~/.local/bin/ralph`로 설치.

---

## P2-2. Plan Iteration UI

### 문제

`ralph --plan` 한 번에 24개 태스크가 튀어나오면 사용자가 검토하기 어렵다. 한 task만 잘못되어도 전체 실행이 망가질 수 있는데 review 인터페이스가 없다.

다른 Ralph 구현체들은 PRD 생성 시:

1. 사용자에게 clarifying questions 제시
2. Executive summary 생성 → 사용자 승인
3. Task list 생성 → 사용자가 각 task review/edit
4. 각 task의 implementation steps 세부화

### 제안

#### 2-1. Interactive plan 모드

```bash
ralph --plan PRD.md --interactive
```

흐름:

```
[1/4] PRD 분석 중...
PRD에서 다음 부분이 모호합니다:
  - "사용자 인증" 부분의 인증 방식이 명시되지 않았습니다.
다음 중 어떤 방식인가요?
  1) JWT
  2) Session-based
  3) OAuth
  4) 기타 (설명)
> 1

[2/4] 작업 구조 제안:
Phase 1 (병렬 4개): add, subtract, multiply, divide
Phase 2 (순차): main
Phase 3 (순차): integration test

이 구조로 진행하시겠습니까? [Y/n/edit]
> Y

[3/4] Task 상세 검토 (24개)
1. add-plan: 덧셈 모듈 계획
   modifiedFiles: ["docs/add-plan.md"]
   prompt: "..."
   [Enter] 승인  [e] 편집  [s] 건너뛰기  [a] 모두 승인
> a

[4/4] tasks.json 생성 완료. 24 tasks, 12 parallel slots.
```

#### 2-2. Plan dry-run with cost estimate

```bash
ralph --plan PRD.md --estimate
```

출력:

```
24 tasks 생성됨. 예상치:
  Token (input):  ~850K  (±20%)
  Token (output): ~250K  (±30%)
  비용 (Sonnet 4): ~$8.50
  실행 시간 (병렬 3): ~45분 (±15분)
```

---

## P2-3. 문서 / 노출 개선

### 문제

- Topic에 `ralph-loop`, `agentic-ai`, `ai-coding`, `prd`가 빠져 있다.
- README가 한국어 전용이라 글로벌 채택이 막힌다.
- 차별화 포인트(병렬 worktree)가 README 상단에 강조되어 있지 않다.

### 제안

#### 3-1. Topics 추가

`ralph-loop`, `agentic-ai`, `ai-coding`, `prd`, `task-orchestrator`, `claude-code`, `autonomous-agent`

#### 3-2. README 영문판

`README.en.md` 추가. 한국어판은 `README.ko.md`로 분리하고 메인 `README.md`는 영문. 첫 문단에서:

```
> The first Ralph implementation with **parallel git worktree execution**.
> Run multiple Claude Code agents simultaneously on independent features,
> with automatic dependency resolution, conflict-aware merging, and live
> progress monitoring.
```

#### 3-3. 비교 표

다른 구현체와 비교 표를 README에 추가:

| Feature | snarktank/ralph | PageAI/ralph-loop | vercel-labs | **starlog/ralph** |
|---------|----------------|-------------------|-------------|------------------|
| Parallel execution | ❌ | ❌ | ❌ | ✅ |
| Windows support | ❌ | ❌ | ❌ | ✅ |
| DAG dependencies | ❌ | partial | ❌ | ✅ |
| Conflict-aware merge | N/A | N/A | N/A | ✅ |
| Single binary | ❌ | ❌ | ❌ | ✅ |

---

## P2-4. 기타 작은 것들

- **`--graph` 출력에 layer별 예상 시간 추가** — 누적된 task별 실행 시간 통계로 예측.
- **`tasks.json`에 `tags` 필드 추가** — `ralph --run --tag backend` 같은 부분 실행 지원.
- **로그 rotation** — `.ralph-logs/`가 무한정 쌓인다. 30일 이상 된 로그 자동 삭제 옵션.
- **`--watch` 모드** — PRD 파일을 watch하다가 변경되면 자동으로 plan 재생성 + 새 task만 실행.
- **Webhook / notification** — Slack, Discord, Telegram에 진행 상황 push. 6시간짜리 AFK 실행 끝났을 때 핸드폰으로 알림 받기.

---

## 구현 순서 권장

가장 임팩트 큰 순서로 한다면:

1. **P0-2 (Verification gate)** — 가장 빠르게 품질 향상이 체감된다.
2. **P0-3 (Cost control)** — Claude Max 사용자에게 immediate value.
3. **P1-1 (modifiedFiles 검증)** — 병렬 실행 안전성. 데이터 손실 방지.
4. **P0-1 (Learning loop)** — 정통 Ralph 정체성 회복.
5. **P2-1 (Pre-built binaries)** — 채택률 즉각 상승.
6. **P1-4 (Crash recovery)** — Long-running 신뢰성.
7. 나머지는 사용자 피드백 보면서.

각 P0 항목은 minor version bump (v0.8, v0.9, v0.10), 모두 끝나면 v1.0.
