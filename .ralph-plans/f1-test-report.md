# F1 테스트 리포트 — `--task` 의존성 검사 + `--force`

- 일시: 2026-04-28
- 대상 구현: `Ralph/Program.cs` (커밋 `af51cfc` 의 `f1-task-deps-impl` 결과물)
- 빌드: `dotnet build Ralph/Ralph.csproj` → 경고 0, 오류 0
- 테스트 환경: macOS (Darwin 25.4.0), .NET 8, `/tmp/ralph-f1-test/tasks.json` 임시 픽스처

## 테스트 픽스처 (`/tmp/ralph-f1-test/tasks.json`)

| ID | done | dependsOn | 용도 |
|---|---|---|---|
| `task-a` | false | (없음) | 의존성 미완료 노드 |
| `task-b` | false | `[task-a]` | 미완료 의존 1개를 가진 시나리오 대상 |
| `task-c` | false | `[task-done]` | 의존성이 모두 done인 시나리오 대상 |
| `task-done` | true | (없음) | 완료된 의존 |
| `task-missing-dep` | false | `[does-not-exist]` | "missing" 상태 출력 검증 |

---

## 시나리오별 결과

### a. 의존성이 모두 완료된 task → 프롬프트 없이 실행 — **PASS**

명령:

```sh
ralph --task task-c < /dev/null
```

관찰:

- "의존성이 완료되지 않았습니다" 경고 출력 **없음**.
- `[3/5] Task ID: task-c` 헤더와 함께 `Executing task: ...` 후 즉시 Claude Code 호출.
- 외부 Claude CLI가 정상 동작하여 task 실행까지 완료(exit 0).

→ `tm.CheckDependencies` 가 `true` 를 반환하여 의존성 분기에 진입하지 않음을 실측 확인.

### b. 미완료 의존 + 대화형 (TTY) → Y/N prompt 출력, N 입력 시 종료 — **PASS (코드 리뷰)**

자동 실행 환경에서는 PTY를 부여하기 어려워 **코드 리뷰 + 비대화형 분기 동작 측정**으로 갈음.

검증 포인트 (`Ralph/Program.cs:402-416`):

- `nonInteractive = Console.IsInputRedirected || Console.IsOutputRedirected`.
  → TTY인 경우 두 값 모두 `false` 이므로 `nonInteractive == false`.
- `AnsiConsole.Confirm("\n[yellow]그래도 진행하시겠습니까?[/]", defaultValue: false)` 호출.
  - `defaultValue: false` → 단순히 `Enter` 만 눌러도 안전 기본값(N)으로 작동.
  - 반환값 `false` → "사용자 취소." 출력 후 `return 1` (exit 1).
  - 반환값 `true` → 진행 메시지 후 `RunTaskAuto(..., force: true)` 호출 (아래 §추가검증 1 참조).
- `taskId`, `depId`, `depTitle` 모두 `Markup.Escape` 처리되어 Spectre.Console 마크업 인젝션 안전.

→ "프롬프트 출력 + N 입력 시 종료" 경로가 코드상 정확히 구성되어 있음.

### c. 미완료 의존 + `--force` → 즉시 실행 — **PASS**

명령:

```sh
echo n | ralph --task task-b --force
ralph --force --task task-b   # (옵션 순서 반대)
```

관찰:

```
⚠️  태스크 'task-b'의 의존성이 완료되지 않았습니다:
  - task-a: First task (pending)
--force 지정됨 — 의존성 무시하고 진행합니다.

────────────────────────────────────────────
[2/5] Task ID: task-b
...
Running Claude Code...
```

- 의존성 목록은 정책상 항상 출력 (force 여부와 무관) — 계획 §4 와 일치.
- `--force` 플래그 인식 후 prompt 없이 즉시 Claude 실행 단계 진입.
- 옵션 순서를 바꾼 `--force --task X` 도 정상 동작 (`argList.Remove` 가 위치 무관).

### d. 미완료 의존 + 비대화형 + `--force` 없음 → exit 1 — **PASS**

명령:

```sh
echo n | ralph --task task-b
```

관찰:

```
⚠️  태스크 'task-b'의 의존성이 완료되지 않았습니다:
  - task-a: First task (pending)

비대화형 환경에서는 --force 없이 의존성을 우회할 수 없습니다.
  예: ralph --task task-b --force
EXIT: 1
```

- 종료 코드 `1` 정확.
- `Console.IsInputRedirected`(파이프 입력) 가 `true` 이므로 prompt 가 띄워지지 않음.
- `--force` 사용을 제안하는 안내 메시지가 함께 출력 — UX OK.

---

## 추가 검증

### 1. **회귀 발견 → 수정 완료**: `RunTaskAuto` 의 중복 의존성 검사

처음 시나리오 c 를 실행했을 때, `--force` 가 `HandleSingleTask` 의 prompt 단계는 통과했으나 직후 호출되는 `RunTaskAuto(...)` 의 자체 dep 검사 (`Program.cs:886`) 에서 다시 막히는 문제가 발생했다:

```
--force 지정됨 — 의존성 무시하고 진행합니다.

Skipping task due to unmet dependencies.
  Blocked by: task-a
```

이는 계획서가 명시한 "force 시 즉시 실행" 수용 기준에 명백히 위배된다.

**수정 내용** (`Ralph/Program.cs`):

1. `RunTaskAuto` 시그니처에 `bool force = false` 매개변수 추가.
   ```csharp
   async Task<int> RunTaskAuto(
       TaskManager tm, ClaudeService claude, GitService git, RalphLogger logger,
       string taskId, bool dryRun, bool commitOnComplete, string? model, CancellationToken ct,
       bool force = false)
   ```
2. 내부 dep 검사를 `if (!force && !tm.CheckDependencies(...))` 로 가드.
3. `HandleSingleTask` 의 호출부에서 `force: true` 를 전달 — 이 시점은 이미 prompt/`--force`/dep-OK 분기 중 하나를 통과한 직후이므로 중복 검사가 본질적으로 불필요.
4. `RunAutoLoop` 의 호출부 (`Program.cs:1013`) 는 `GetNextReadyTask` 로 이미 ready 한 태스크만 골라오므로 기본값 `false` 를 그대로 사용 — 이 경로의 동작은 회귀 없음.

수정 후 재빌드 (경고 0 / 오류 0) 및 c, a 시나리오 재실행 → 모두 PASS.

### 2. 의존성 상태 라벨 (`pending` / `done` / `missing`)

| 상황 | 표시 | 검증 |
|---|---|---|
| 의존이 `Done == false` | `(pending)` | task-b 결과에서 확인 |
| 의존 ID 가 tasks.json 에 없음 | `(missing)` 및 제목 `(unknown)` | `task-missing-dep` 결과에서 확인 |
| 의존 `Done == true` | (분기 자체 미진입) | `task-c` 시나리오에서 prompt 미출력으로 확인 |

계획서 §0 의 결정대로 "failed" 상태는 모델 미지원 항목으로 N/A.

### 3. `--help` 텍스트 정합성

`Program.cs:762`, `Program.cs:783` 의 `--task` / `--force` 설명이 실제 동작과 일치 — 변경 불필요.

---

## 시나리오 결과 요약

| # | 시나리오 | 결과 |
|---|---|---|
| a | 의존 완료 → 즉시 실행 | PASS |
| b | TTY + 미완료 의존 + N | PASS (코드 리뷰) |
| c | 미완료 의존 + `--force` | PASS (회귀 수정 후) |
| d | 비대화형 + 미완료 의존 + force 없음 → exit 1 | PASS |

---

## 생성/수정한 파일

- `.ralph-plans/f1-test-report.md` — 본 리포트 (신규).
- `Ralph/Program.cs` — `RunTaskAuto` 의 중복 dep 검사 회귀 수정 (Scope 內 명시 파일). 변경 라인:
  - `HandleSingleTask` 의 `RunTaskAuto` 호출부 (`force: true` 인자 전달).
  - `RunTaskAuto` 시그니처에 `bool force = false` 추가 및 dep 검사 가드.

## Scope 외 변경 사유

없음 — 모든 변경은 작업 지시에 명시된 수정 가능 파일(`Ralph/Program.cs`, `.ralph-plans/f1-test-report.md`) 범위 내에서 이루어졌다.

`tasks.json` 은 일절 건드리지 않았으며, 테스트용 픽스처는 worktree 외부의 `/tmp/ralph-f1-test/tasks.json` 에 별도 작성하여 worktree 격리/머지 충돌을 방지했다.
