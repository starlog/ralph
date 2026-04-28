# F1 (P0) — `--task <id>` 의존성 검사 + `--force` 분리 구현 계획

## 0. 배경 / 현재 상태 진단

코드베이스 정독 결과, **이 기능의 골격은 이미 부분 구현되어 있으나 요구사항을 완전히 만족하지 못한다**. 본 계획은 신규 구축이 아니라 기존 로직의 보강 위주로 작성한다.

### 현재 구현 상태 (Ralph/Program.cs)

- `--force` 플래그 파싱: **이미 존재** — `Program.cs:41` `var forceFlag = argList.Remove("--force");`
- `HandleSingleTask` 의 의존성 검사: **이미 존재** — `Program.cs:383-406`
- `tm.CheckDependencies(taskId, out var blockedBy)`: **이미 존재** — `Ralph/Services/TaskManager.cs:84-99`
- ShowHelp 의 `--force` 안내: **이미 존재** — `Program.cs:771`

### 요구사항 대비 부족한 점 (이번에 구현해야 할 것)

| 요구사항 | 현재 동작 | 필요한 변경 |
|---|---|---|
| 미완료 의존성 출력 | ✅ 출력함 | (수정 없음) |
| TTY 환경에서 Y/N prompt | ❌ prompt 없이 바로 exit 1 | **`AnsiConsole.Confirm` 추가** |
| 비대화형(redirect) 환경에서 prompt 생략 | ❌ TTY 여부 미구분 | **`Console.IsInputRedirected` 분기 추가** |
| 비대화형 + `--force` 없음 → exit 1 | ✅ (사실상) exit 1 | (의도 동일 — 코드 명시화) |
| 의존성 항목에 ID + 상태 표시 | ⚠️ "done/pending/missing" 사용 | **"pending/failed" 표기 정렬 (※ 주의 필요)** |

### 모델상의 제약 (반드시 인지)

`Ralph/Models/TasksFile.cs:42` 의 `TaskItem` 은 `Done : bool` 하나만 가진다. **"failed" 상태는 데이터 모델에 존재하지 않는다.** 즉 _완료되지 않은 태스크_ 는 항상 "pending" 으로 표시된다. 요구사항이 명시한 "pending/failed" 분기는 _아직 모델 차원의 실패 표기가 없으므로 현 단계에서는 의미상 매핑 불가_ 다.

→ **결정**: 표시 상태는 `done` / `pending` / `missing`(존재하지 않는 ID) 3종으로 유지하되, 향후 F-시리즈에서 실패 상태가 도입되면 동일 위치에서 "failed" 매핑을 추가한다 (TODO 주석으로 남김). 이 결정의 이유는 "tasks.json 모델 변경이 본 태스크의 Scope를 벗어나며, 다른 worktree(F2 등)에서 동시에 변경 중이기 때문" 이다.

---

## 1. 변경 대상 파일과 함수 시그니처

### 변경 대상

| 파일 | 함수/위치 | 변경 종류 |
|---|---|---|
| `Ralph/Program.cs` | `HandleSingleTask` (현재 라인 ~364-414) | **수정** — TTY 분기 + Confirm prompt 추가 |

### 변경 비대상 (수정 금지)

- `Ralph/Services/TaskManager.cs` — 기존 `CheckDependencies` 가 충분하므로 신규 메서드 불필요. (동시 worktree 머지 충돌 방지)
- `Ralph/Models/TasksFile.cs` — TaskItem 모델 변경은 본 태스크 Scope 외.
- `tasks.json` — 절대 금지 (worktree 격리).

### 시그니처는 그대로 유지

`async Task<int> HandleSingleTask()` — top-level statement 의 클로저 함수이므로 기존 시그니처 변경 불필요. `forceFlag` 변수는 이미 클로저로 캡처됨 (`Program.cs:41` 에서 선언).

---

## 2. `--force` 플래그 파싱 흐름 (이미 구현됨, 검증만)

```csharp
// Program.cs:41 (이미 존재)
var forceFlag = argList.Remove("--force");
```

- 글로벌 플래그로서 `argList.Remove` 가 위치 무관하게 첫 매치를 제거.
- 이 변수는 클로저 캡처되어 `HandleRun` (`Program.cs:218`), `HandleValidate` (`Program.cs:528`), `HandleSingleTask` (`Program.cs:396`) 에서 이미 사용 중.
- **추가 파싱 작업 없음**. 단, F1 검증 시 `ralph --task X --force` 와 `ralph --force --task X` 둘 다 정상 동작하는지 확인.

---

## 3. 의존성 미완료 감지 로직

기존 `TaskManager.CheckDependencies` 를 그대로 사용. 신규 API 불필요.

```csharp
// 호출 예 (이미 Program.cs:384 에서 사용 중)
if (!tm.CheckDependencies(taskId, out var blockedBy))
{
    // blockedBy: 미완료/누락 의존 ID 리스트
}
```

- `CheckDependencies` 동작:
  - 의존성 없음 → `true` 반환 (정상 케이스)
  - 의존 ID 가 존재하지 않거나 `!Done` → `blockedBy` 에 해당 ID 추가, `false` 반환

각 `blockedBy` 항목의 상태 매핑은 호출부에서 `tm.GetTask(depId)` 로 조회하여 다음과 같이 분류한다:
- `tm.GetTask(depId) == null` → 표시 `missing`
- `dep.Done == true` → 표시 `done` (실제로는 이 분기 도달 안 함, 방어적)
- 그 외 → 표시 `pending`

---

## 4. TTY / 비대화형 분기 의사코드

### 분기 정의

비대화형(non-interactive)으로 간주하는 조건 — **OR 조합**:
- `Console.IsInputRedirected == true` (입력이 파이프/파일/리다이렉트)
- `Console.IsOutputRedirected == true` (출력이 파이프/파일 — CI 로그 캡처 등)

→ 둘 중 하나라도 참이면 prompt 띄우지 않음.

### 의사코드 (변경 후 `HandleSingleTask` 의 의존성 검사 블록)

```csharp
// Program.cs:383~ 부근 (의존성 검사 블록)
if (!tm.CheckDependencies(taskId, out var blockedBy))
{
    // 1) 의존성 목록 출력 (ID + 상태)
    AnsiConsole.MarkupLine(
        $"\n[yellow]⚠️  태스크 '{Markup.Escape(taskId)}'의 의존성이 완료되지 않았습니다:[/]");
    foreach (var depId in blockedBy)
    {
        var dep = tm.GetTask(depId);
        var depTitle = dep?.Title ?? "(unknown)";
        // 상태 결정: missing / done(방어) / pending
        // TODO(F-future): 모델에 실패 상태 도입 시 "failed" 추가
        var status = dep == null
            ? "missing"
            : (dep.Done ? "done" : "pending");
        AnsiConsole.MarkupLine(
            $"  - {Markup.Escape(depId)}: {Markup.Escape(depTitle)} [dim]({status})[/]");
    }

    // 2) --force 면 즉시 우회
    if (forceFlag)
    {
        AnsiConsole.MarkupLine("[yellow]--force 지정됨 — 의존성 무시하고 진행합니다.[/]\n");
    }
    else
    {
        // 3) 비대화형이면 prompt 없이 exit 1
        var nonInteractive = Console.IsInputRedirected || Console.IsOutputRedirected;
        if (nonInteractive)
        {
            AnsiConsole.MarkupLine(
                "\n[red]비대화형 환경에서는 --force 없이 의존성을 우회할 수 없습니다.[/]");
            AnsiConsole.MarkupLine(
                $"  예: [cyan]ralph --task {Markup.Escape(taskId)} --force[/]");
            return 1;
        }

        // 4) TTY 면 Y/N prompt
        var proceed = AnsiConsole.Confirm(
            "\n[yellow]그래도 진행하시겠습니까?[/]",
            defaultValue: false); // 기본값 N — 안전 우선
        if (!proceed)
        {
            AnsiConsole.MarkupLine("[dim]사용자 취소.[/]");
            return 1;
        }
        AnsiConsole.MarkupLine("[yellow]사용자 확인 — 의존성 무시하고 진행합니다.[/]\n");
    }
}

// 이후 ClaudeService/GitService/Logger 생성 및 RunTaskAuto 호출 (기존과 동일)
```

### 배치 순서 — 왜 출력 먼저, 분기 나중인가
의존성 목록은 **`--force` 여부, TTY 여부와 무관하게 항상 사용자에게 보여주는 것이 정책상 안전**하다. 사용자가 `--force` 를 무심코 붙였더라도 어떤 의존이 비어있는지 로그에 남는다.

---

## 5. Spectre.Console prompt 패턴

이미 코드베이스 내 사용 중인 `AnsiConsole.Confirm` 동일 패턴 차용:

```csharp
// 참고 — Program.cs:1089 (RunInteractiveLoop 내)
if (!AnsiConsole.Confirm("Continue anyway?", defaultValue: false))
{
    logger.TaskEnd(nextId, "failed");
    return 1;
}
```

### 본 태스크에서의 사용

- `defaultValue: false` (Y/N 기본 N — 안전한 기본값)
- 메시지는 한국어 (CLAUDE.md 규약 준수)
- `AnsiConsole.Confirm` 은 내부적으로 stdin 사용 → **`Console.IsInputRedirected` 분기로 사전 차단되므로 실행 시점에 prompt 가 깨지지 않음** (방어 OK)

### `SelectionPrompt` 대신 `Confirm` 을 쓰는 이유

`RunInteractiveLoop` (`Program.cs:1066`) 는 `SelectionPrompt<string>` 으로 4지선다(Yes/Preview/Skip/Quit) 를 쓰지만, 본 위치는 **단순 Y/N 만 필요**하므로 `Confirm` 이 적합. UX 일관성보다 단순성 우선.

---

## 6. 변경 후 `HandleSingleTask` 전체 흐름 (요약)

1. `argList.Count < 2` → 사용법 출력 후 exit 1
2. `taskId = argList[1]` 추출
3. `tasksFile` 존재 확인 (`RequireFile`)
4. `TaskManager.LoadAsync(tasksFile)` → `tm`
5. `tm.GetTask(taskId)` 가 null 이면 "Task not found" exit 1
6. **[변경 영역]** `tm.CheckDependencies(taskId, out blockedBy)`:
   - 모두 충족 → 통과 (현 동작 유지)
   - 미충족:
     a. blockedBy 목록을 ID + 상태 와 함께 출력
     b. `forceFlag == true` → 경고 후 진행
     c. `forceFlag == false` && 비대화형 → exit 1
     d. `forceFlag == false` && TTY → `AnsiConsole.Confirm` Y/N
        - Y → 진행
        - N → exit 1
7. `ClaudeService` / `GitService` / `RalphLogger` 생성
8. `RunTaskAuto(...)` 호출 후 결과 반환

---

## 7. 회귀 위험과 검증 시나리오

### 회귀 시나리오 매트릭스

| # | 케이스 | 기대 동작 | 검증 방법 |
|---|---|---|---|
| 1 | 의존성 없는 태스크 (`DependsOn == null` 또는 빈 리스트) | prompt 없이 정상 실행 | `CheckDependencies` 가 `true` 반환 → 분기 미진입 |
| 2 | 의존성 모두 done 상태 | prompt 없이 정상 실행 | 동일하게 `true` 반환 |
| 3 | 의존성 1개 pending, TTY, --force 없음, 사용자 Y | 진행 | 통합테스트 필요 (수동 실행) |
| 4 | 의존성 1개 pending, TTY, --force 없음, 사용자 N | exit 1, 코드 변경 없음 | 동일 |
| 5 | 의존성 1개 pending, TTY, --force 있음 | prompt 없이 진행 | 동일 |
| 6 | 의존성 1개 pending, 비대화형(`echo y \| ralph --task X`) | exit 1 (prompt 띄우지 않음) | shell pipe 로 검증 |
| 7 | 의존성 1개 pending, 비대화형 + --force | 진행 | CI 환경 시뮬레이션 |
| 8 | 의존 ID 가 tasks.json 에 없음 (typo / 삭제) | "missing" 으로 표시 | 의도적 잘못된 dep 추가 후 확인 |
| 9 | `ralph --force --task X` (순서 반대) | 정상 동작 | `argList.Remove` 는 위치 무관 |

### 다른 명령과의 상호작용

- **`--interactive` 모드** (`HandleInteractive` → `RunInteractiveLoop`): 영향 없음. `RunInteractiveLoop` 내부에 별도 의존성 처리 (`Program.cs:1052-1058`) 가 있고 `HandleSingleTask` 와 분리됨. 두 경로는 독립.
- **`--run` 모드**: 영향 없음. `HandleRun` 은 자체적으로 `forceFlag` 를 plan validation 우회용으로만 사용 (`Program.cs:218`); 의존성은 `RunAutoLoop` 의 `GetNextReadyTask` 로 자연스럽게 다음으로 넘어감.
- **`--dry-run` 모드**: 영향 없음. `HandleDryRun` 은 단일 태스크 경로를 거치지 않음.
- **병렬 실행 (`ParallelExecutor`)**: 영향 없음. 단일 태스크 경로와 분리.

### 잠재적 공격 표면

- `Console.IsInputRedirected` 는 `dotnet test` / VSCode 디버그 콘솔 등 일부 IDE 환경에서 `true` 로 잡힐 수 있음. 그 경우 prompt 가 안 뜨고 exit 1 됨 → **사용자가 의외라 느낄 수 있다**. 보완: 메시지에 "비대화형 감지됨; --force 사용" 안내 명시 (위 의사코드 반영).
- `AnsiConsole.Confirm` 은 `ESC` / `Ctrl+C` 입력 시 예외를 던질 수 있음. 상위에 `Program.cs:115-119` 의 `OperationCanceledException` 핸들러가 있어 exit 130 으로 정상 종료됨 (검증 OK).

### Markup 이스케이프

`Markup.Escape` 는 모든 사용자 입력 문자열 (taskId, depId, depTitle) 에 적용. 누락 시 Spectre.Console 가 throw 함. 의사코드 전 구간 적용 확인.

---

## 8. 구현 시 체크리스트 (다음 단계 태스크 — `f1-task-deps-impl` 용)

- [ ] `Program.cs:383~406` 영역을 위 §4 의사코드로 교체
- [ ] 의사코드의 한국어 메시지 톤이 다른 메시지(`Program.cs:215, 220, 401-403`)와 일관적인지 확인
- [ ] 빌드 (`dotnet build Ralph/Ralph.csproj`) 통과 확인
- [ ] 위 §7 매트릭스의 케이스 1, 2, 5, 6 은 자동 검증 가능 (스크립트 작성 가능)
- [ ] `--help` 의 `--force` 설명 (`Program.cs:771`) 이 여전히 정확한지 점검 (현재 "Bypass dependency/validation checks (--task, --run)" — 정확함, 변경 불필요)
- [ ] `tasks.json` 은 절대 수정 금지 (worktree 격리)
- [ ] 다른 plan 파일(`f2-worktree-tasksjson.md`, `README.md`, `README.ko.md`) 도 건드리지 않음

---

## 9. Out of Scope (이번 태스크에서 하지 않는 것)

- `TaskItem` 에 `Failed` 상태 추가 — 모델 변경은 별도 P1/P2 태스크.
- `--force` 의 의미를 `--task` 외 다른 명령에서 통일 — 이미 `--run` `--validate` 도 사용 중이므로 호환성 유지.
- 의존성 자동 실행 (cascade) 기능 — F1 의 범위가 아님 (`--task X` 는 단일 태스크 실행 보장).
- `Console.IsInputRedirected` 의 cross-platform 호환성 검증 — .NET 8 표준 API 라 Windows/macOS/Linux 동작 보증됨.

---

## 10. 결론

이 태스크의 실질적 코드 변경은 **`Program.cs:383-406` 한 블록의 보강**이다. 신규 메서드 추가 없이, 기존 `forceFlag` 와 `tm.CheckDependencies` 를 활용해 `Console.IsInputRedirected || Console.IsOutputRedirected` 분기 + `AnsiConsole.Confirm` Y/N prompt 만 끼워넣으면 된다. 회귀 위험은 낮고 (의존성 없는 / 충족된 케이스는 분기 미진입), `--interactive` `--run` 등과 경로가 분리되어 있어 부수 효과가 없다.
