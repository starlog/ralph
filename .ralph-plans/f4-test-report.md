# F4 테스트: modifiedFiles 검증 시나리오

대상 구현: `f4-modified-files-validate-impl`
검증 대상 파일: `Ralph/Services/WorktreeService.cs`, `Ralph/Services/ParallelExecutor.cs`, `Ralph/Program.cs`

## 1. 빌드 검증

| 항목 | 결과 |
|---|---|
| `dotnet build Ralph/Ralph.csproj` | **PASS** — 경고 0, 오류 0 |
| 산출물 | `bin/Debug/net8.0/osx-arm64/ralph.dll` |

## 2. 코드 리뷰

### 2.1 `WorktreeService.ValidateModifiedFilesAsync` — PASS

`Ralph/Services/WorktreeService.cs:223-285`의 구현 검토 결과 PRD §3.1 / §3.3 시퀀스를 모두 충족.

| 항목 | 위치 | 결과 |
|---|---|---|
| 시그니처가 PRD §3.1과 일치 (`taskId`, `baseRef`, `declared`, `logger`, `validationLogPath`, `ct`) | line 223–229 | PASS |
| git diff 명령: `git -C {worktreePath} diff --name-only {baseRef}..HEAD` (pathspec 없음) | line 234–235 | PASS |
| cwd가 `Path.GetFullPath(Path.Combine(_worktreeBase, taskId))` (F2 패턴 동일) | line 231 | PASS |
| diff 실패 시 `DiffFailed=true`로 머지 강행 + Warn 로그 | line 237–246 | PASS |
| `actual` 정규화·정렬: trim → `NormalizeSlash` → `Distinct(Ordinal)` → `OrderBy(Ordinal)` | line 248–255 | PASS |
| `declared` 정규화·정렬: 빈 문자열 제거 → `NormalizeSlash` → `Distinct(Ordinal)` → `OrderBy(Ordinal)` | line 257–262 | PASS |
| `undeclared = actual − declared`, `notChanged = declared − actual` | line 267–268 | PASS |
| `undeclared`만 Warn 로그 (앞 3개 + "외 N건" 요약) | line 270–276 | PASS |
| 결과 record 반환 + JSONL append | line 278–284 | PASS |
| `NormalizeSlash`로 Windows `\` → `/` 통일 | line 287–288 | PASS |

### 2.2 `validation.jsonl` 형식 — PASS

`AppendValidationLogAsync` (line 290–316) + `ValidationLogEntry` (line 318–324):

- 직렬화 옵션: `JsonNamingPolicy.CamelCase`, `WriteIndented=false` (line 32–36) → camelCase 한 줄 JSON.
- 필드: `taskId`, `timestamp` (`DateTimeOffset.UtcNow.ToString("o")` ISO-8601 with offset), `declared`, `actual`, `undeclared`, `notChanged`. **PRD §3.4 스펙과 일치**.
- `Directory.CreateDirectory(dir)`로 부모 디렉토리 보장(line 296–298).
- `File.AppendAllTextAsync(path, line + "\n", ct)` — `cost.jsonl` 패턴과 일관(line 309).
- 직렬화/IO 예외는 `try/catch`로 머지 흐름과 격리(line 311–315). best-effort 정책 일치.

> 참고: `ToString("o")`은 UTC 기준 `DateTimeOffset`이라 결과가 `...+00:00`로 끝난다. PRD §3.4 예시는 `Z` suffix지만 ISO-8601 호환이며 `DateTimeOffset.Parse`로 라운드트립 가능하므로 본 PR 범위에서 spec 일치로 판정.

### 2.3 `ParallelExecutor` 호출 순서 — PASS

`Ralph/Services/ParallelExecutor.cs:267-303`의 머지 루프:

```text
foreach taskId in taskIds:
  L274  NormalizeTasksJsonAsync(...)   ← F2
  L283  ValidateModifiedFilesAsync(...) ← F4
  L286  ReportValidation(...)
  L288  if (_strictFiles && HasUndeclared) return 1   ← strict 차단
  L303  MergeWorktreeAsync(...)
```

PRD §3.5의 `NormalizeTasksJsonAsync → ValidateModifiedFilesAsync → MergeWorktreeAsync` 순서를 정확히 충족. 코드 주석(line 271–281)도 "F2 정규화 이후에 호출되어야 tasks.json이 actual에서 빠진다"라고 명시.

### 2.4 `--strict-files` 플래그 전달 경로 — PASS

| 단계 | 위치 | 결과 |
|---|---|---|
| env 파싱 `RALPH_STRICT_FILES=true` | `Program.cs:32` | PASS |
| CLI 파싱 `argList.Remove("--strict-files") || envStrictFiles` | `Program.cs:43` | PASS |
| `ParallelExecutor` 생성자에 `strictFiles: strictFiles` 전달 | `Program.cs:276-277` | PASS |
| 생성자 시그니처에 `bool strictFiles = false` + `_strictFiles` 필드 | `ParallelExecutor.cs:15, 22, 31` | PASS |
| `ShowHelp` 본문에 `--strict-files` 옵션 라인 + `RALPH_STRICT_FILES` 환경변수 라인 | `Program.cs:788, 803` | PASS |

### 2.5 strict 모드 차단 로직 — PASS

`ParallelExecutor.cs:288-301`:

- `_strictFiles && validation.HasUndeclared` 충족 시 **`MergeWorktreeAsync` 미호출** + `return 1`.
- 콘솔 출력: `[red]✗[/] {taskId} undeclared 파일 N건. 머지 중단 (strict-files): {앞3개...}`.
- `_logger.Error`로 위반 파일 전체 목록 기록.
- `finally` 블록(line 340-348)이 모든 worktree 정리. PRD §3.6 정책 일치.
- 차단 시 `MarkTaskDoneThreadSafe` 미호출(머지 안 했으므로) — 다음 실행에서 재시도 가능. **의도된 동작**.

> 미세 노트(회귀는 아님): strict 차단이 batch 중간 태스크에서 발생하면, 그 태스크 *이전*에 이미 성공적으로 머지된 태스크들의 `done` 상태도 같이 미마킹된다(상태 마킹은 머지 루프 종료 후 step 4에서 한꺼번에 일어나므로). PRD §5에서 인지된 "처리한 부분은 보존, 실패 태스크는 미완료" 정책과는 약간 어긋나지만 본 태스크 수용 기준에는 포함되지 않으므로 PASS로 판정. 별도 P2 후보.

### 2.6 `ReportValidation` 분기 — PASS

`ParallelExecutor.cs:642-670`:

- `DiffFailed`: `[yellow]⚠[/] diff 실패 — 검증 스킵`.
- `HasUndeclared && !_strictFiles`: `[yellow]⚠[/] undeclared N건 (warn-only)` (strict 모드에서는 별도 분기에서 출력하므로 중복 없음).
- `HasNotChanged`: `[dim]ℹ notChanged N건` (strict 무관 항상 표시).
- 첫 3개 + "외 N건" 줄임 표시 일관.

## 3. 시나리오별 동작 분석

표기: 시뮬레이션 없이 코드 경로 추적(static review).

### 3a. declared와 actual 정확히 일치 — PASS

조건: `task.ModifiedFiles ∪ task.OutputFiles` = `git diff base..HEAD` 결과.

코드 경로:
1. `ValidateModifiedFilesAsync`:
   - `actual.Where(p => !declaredSet.Contains(p))` → `undeclared = []`.
   - `declaredNorm.Where(p => !actualSet.Contains(p))` → `notChanged = []`.
   - `undeclared.Count > 0` 거짓 → **Warn 로그 없음**.
   - `AppendValidationLogAsync` 호출 → JSONL **1줄 누적** (declared/actual 동일, undeclared/notChanged 빈 배열).
2. `ReportValidation`: `DiffFailed=false`, `HasUndeclared=false`, `HasNotChanged=false` → **콘솔 출력 없음**.
3. `_strictFiles` 분기 미진입 → `MergeWorktreeAsync` 정상 호출 → 머지 진행.

판정: **PASS**. 단, 수용 기준의 "로그 없음"은 RalphLogger의 Warn/Error/Info를 의미하는 것으로 해석 — JSONL은 누적 trace 용도이므로 항상 1줄 기록되는 게 사양(`AppendValidationLogAsync`는 `DiffFailed`가 아닌 한 무조건 호출). 운영자 입장의 "조용한 정상 케이스"는 만족.

### 3b. Undeclared 존재 + 기본 모드 (warn-only) — PASS

조건: declared=`[A]`, actual=`[A, B]`, `_strictFiles=false`.

코드 경로:
1. `ValidateModifiedFilesAsync`:
   - `undeclared = [B]`, `notChanged = []`.
   - `undeclared.Count > 0` → `_logger.Warn("[validate:files] {taskId}: undeclared 1건 — B")` 1줄.
   - JSONL 1줄 누적: `{"taskId":...,"declared":["A"],"actual":["A","B"],"undeclared":["B"],"notChanged":[]}`.
2. `ReportValidation`: `HasUndeclared && !_strictFiles` 진입 → `[yellow]⚠[/] undeclared 1건 (warn-only): B` 콘솔 출력.
3. `_strictFiles=false`이므로 strict 분기 미진입 → `MergeWorktreeAsync` 호출 → **머지 진행**.

판정: **PASS** (Warn 로그 1줄 + JSONL 1줄 + 머지 진행).

### 3c. Undeclared 존재 + `--strict-files` — PASS

조건: declared=`[A]`, actual=`[A, B]`, `_strictFiles=true`.

코드 경로:
1. `ValidateModifiedFilesAsync` 동일 — `undeclared=[B]`. Warn 로그 + JSONL 1줄.
2. `ReportValidation`: `HasUndeclared && !_strictFiles` 거짓 → 해당 분기 미출력(중복 방지).
3. `if (_strictFiles && validation.HasUndeclared)` 진입:
   - 콘솔: `[red]✗[/] {taskId} undeclared 파일 1건. 머지 중단 (strict-files): B`.
   - `_logger.Error("[validate:files][strict] {taskId} undeclared: B")`.
   - **`MergeWorktreeAsync` 미호출**.
   - `return 1` → `RunParallelBatchAsync` 종료 → `RunAsync`도 종료, exit 1.
4. `finally` 블록이 batch 내 모든 worktree 정리.
5. `MarkTaskDoneThreadSafe` 미호출 → `tasks.json`의 해당 태스크 `done` 유지(false). **task failed로 표시**.

판정: **PASS** (머지 skip + return 1 + 태스크 미완료 + worktree 정리).

### 3d. NotChanged만 존재 — PASS

조건: declared=`[A, B]`, actual=`[A]`, `_strictFiles=true` 또는 `false`.

코드 경로:
1. `ValidateModifiedFilesAsync`:
   - `undeclared = []`, `notChanged = [B]`.
   - `undeclared.Count > 0` 거짓 → Warn 로그 없음.
   - JSONL 1줄 누적: `{"declared":["A","B"],"actual":["A"],"undeclared":[],"notChanged":["B"]}`.
2. `ReportValidation`:
   - `HasUndeclared` 거짓 → undeclared 콘솔 미출력.
   - `HasNotChanged` 참 → `[dim]ℹ notChanged 1건: B` 출력.
3. `_strictFiles && HasUndeclared` 거짓 → strict 분기 **미진입** (notChanged는 차단 사유 아님, PRD §3.7 정책 일치).
4. `MergeWorktreeAsync` 호출 → **머지 진행**.

판정: **PASS** (JSONL 기록 + 콘솔 dim 표시 + strict 무관 머지 진행).

## 4. 종합

| 항목 | 결과 |
|---|---|
| 1. `dotnet build` 통과 | **PASS** |
| 2. `ValidateModifiedFilesAsync` git/cwd/declared 정규화 | **PASS** |
| 3. `validation.jsonl` 필드 일치 | **PASS** |
| 4. F2 → F4 → merge 호출 순서 | **PASS** |
| 5. `--strict-files` 파싱·전달 경로 | **PASS** |
| 6. strict 모드에서 Undeclared 발견 시 머지 skip + task failed | **PASS** |
| 7a. declared = actual | **PASS** |
| 7b. Undeclared + 기본 모드 | **PASS** |
| 7c. Undeclared + strict | **PASS** |
| 7d. NotChanged만 존재 | **PASS** |

수용 기준 7개 항목 전부 충족. 소스 수정은 필요하지 않음(이번 테스트 태스크에서 변경한 소스 없음).

## 5. 후속 관찰 (별도 과제 후보)

- strict 차단 시 batch 내 *이전* 머지 성공 태스크의 `done` 마킹 누락 가능성 (§2.5 노트). PRD §5에서 인지됨, 본 수용 기준 외.
- `validation.jsonl` 무한 누적 회전 — PRD §3.4·§7에서 본 PR 외로 명시.
- `git diff --name-only`의 rename 케이스 두 줄 출력 — PRD §5 위험표에 기재, 본 PR 외.

## 6. 보고

- 생성/수정한 파일: `/Users/felix/src/_tool/ralph/.ralph-plans/f4-test-report.md` (신규).
- Scope 외 파일 변경: 없음.
- `tasks.json` 수정: 없음.
