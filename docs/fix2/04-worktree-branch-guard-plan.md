# Fix2 #4 — Worktree 브랜치 삭제 가드 이중화 설계

## 1. 배경

`Ralph/Services/WorktreeService.cs`의 `IsRalphManagedBranchAsync`(L551-586)는
다음 두 신호 중 하나만 충족하면 ralph 소유로 판정하고 `branch -D`를 허용한다.

1. `branch.{name}.ralphManaged=true` git config 마커가 존재 (1차 신호).
2. `git worktree list --porcelain`에서 해당 브랜치가 `.ralph-worktrees/` 산하
   워크트리에 바인딩되어 있음 (legacy fallback).

`fix2.md` #4에 정리된 위험 케이스:

- config가 외부 도구(`git config --remove-section`, fresh clone, repo 복제)로
  손실 → 사용자가 만든 동일 패턴 브랜치를 ralph 브랜치로 오판.
- 사용자가 `ralph/feature-x`처럼 우연히 같은 prefix로 자기 브랜치를 생성.
- 워크트리 삭제 후 마커만 남고 워크트리는 사라진 잔여 상태에서, 외부
  도구가 `ralph/foo` 브랜치를 직접 만들어 점유.

가드의 안전 등급을 "OR 둘 중 하나" → "(1 OR 2) AND 3"으로 강화하되, false
positive(정상 ralph 워크트리도 못 지우는 회귀)를 막기 위해 3번 조건은
**식별 가능한 경우에만** 강제한다.

---

## 2. 현재 가드 로직 흐름도

```
CleanupWorktreeAsync(taskId)                    [WorktreeService.cs:622]
  │
  ├─ branchExists  ← BranchExistsAsync (refs/heads/{name})
  ├─ branchManaged ← branchExists && IsRalphManagedBranchAsync
  │                                       ├─ config: branch.{n}.ralphManaged=true
  │                                       └─ legacy: worktree list 바인딩
  │
  ├─ git worktree remove --force         (디렉터리 정리, 항상 시도)
  │
  └─ if (BranchExistsAsync 재확인)
        if (branchManaged) → git branch -D {name}     ◀── 여기가 위험 지점
        else               → "ralph 표시 없음" 보존 + 안내

CreateWorktreeAsync(taskId)                     [WorktreeService.cs:68]
  │
  ├─ 동명 브랜치 존재 시 IsRalphManagedBranchAsync로 가드 후 -D
  ├─ git worktree add -b ralph/{taskId} ...
  └─ MarkRalphManagedAsync(branchName)   ← 1차 신호 설정 (config만)
```

문제: **ralphManaged config 단독**만 보고 `-D`까지 진행한다. 사용자가
실수로 같은 이름 브랜치를 만든 직후 ralph가 cleanup을 돌리면, fresh clone
환경에서는 마커도 없어 보존되지만, 마커가 한 번이라도 박혔던 brick repo
에서는 잘못 삭제될 수 있다.

---

## 3. 보강 설계: (1 OR 2) AND 3 모델

### 3.1 신호 정의

| 신호 | 의미 | 출처 |
|---|---|---|
| **A. config 마커** | `branch.{name}.ralphManaged=true` | `git config --get` |
| **B. 활성 워크트리 바인딩** | 브랜치가 `.ralph-worktrees/` 하위 워크트리에 현재 묶여 있음 | `git worktree list --porcelain` |
| **C. ralph 시그니처 커밋** | 브랜치 reflog의 첫(최오래된) entry 커밋이 ralph가 만든 것 | `git reflog --no-abbrev refs/heads/{name}` + `git log -1 --format=%(trailers:key=Ralph-Task-Id)` |
| **D. .ralph-marker 파일** | 워크트리 루트의 `.ralph-marker` 파일 (워크트리가 살아 있는 동안만 의미) | 파일 존재 + 내용 검증 |

판정 규칙:

```
isManaged = (A OR B) AND (C OR D OR signature_unknown)
```

- `A OR B`는 기존 로직 유지(이미 이중화되어 있어 약화하지 않음).
- `C OR D`는 **추가 안전 검증**. 둘 중 하나라도 ralph 시그니처를 확인하면
  통과. 둘 다 확인 불가(=식별 불가)일 때는 `signature_unknown`로
  취급하고 보수적으로 통과 — fix2.md 요구사항의 "식별 가능한 경우" 단서를
  반영한다. 시그니처가 명확히 **사용자 커밋**으로 식별되면 무조건 차단.

상태 매트릭스:

| A/B | C/D 상태 | 결과 |
|---|---|---|
| true | ralph 시그니처 확인 | **삭제 진행** (정상 케이스) |
| true | 사용자 시그니처 확인 | **삭제 보류** + 경고 (fix2 핵심 케이스) |
| true | 식별 불가(reflog 미보존, 마커 부재 등) | **삭제 보류** + 경고 (수동 정리 안내) |
| false | — | **삭제 안 함** + "ralph가 만든 게 아님" 메시지 (기존 동작) |

> 보수적 선택: fix2.md 요구사항 "불확실 시 삭제 보류"를 그대로 따른다.
> 정상 ralph 워크트리는 §3.4의 추가 마킹(commit trailer + .ralph-marker)
> 으로 거의 항상 C 또는 D를 만족하므로 회귀 위험이 낮다.
> 마커 도입 이전 버전이 만든 잔존 브랜치는 reflog가 만료된 경우 보류 →
> 사용자가 한 번 수동 삭제하면 자연 정리되며, 데이터 손실 방향이 안전하다.

### 3.2 의사코드

```csharp
// 새 메서드. IsRalphManagedBranchAsync는 그대로 두고, 삭제 직전 한 번 더
// 호출되는 "안전 검증" 레이어를 추가한다.
private async Task<BranchSafeDeleteVerdict> VerifySafeToDeleteAsync(
    string branchName, string taskId, CancellationToken ct)
{
    // (1) A OR B — 기존 IsRalphManagedBranchAsync 결과 그대로 사용
    if (!await IsRalphManagedBranchAsync(branchName, ct))
        return BranchSafeDeleteVerdict.NotRalphManaged;       // 메시지 없음 (기존 흐름)

    // (2) C — reflog 첫 entry의 커밋이 ralph 시그니처를 가지는지
    var sig = await ProbeRalphSignatureAsync(branchName, taskId, ct);

    // (3) D — 워크트리 루트의 .ralph-marker (있으면 즉시 통과)
    var marker = await ProbeMarkerFileAsync(taskId, branchName, ct);

    return (sig, marker) switch
    {
        (Signature.Ralph, _)        => BranchSafeDeleteVerdict.SafeToDelete,
        (_, MarkerState.RalphValid) => BranchSafeDeleteVerdict.SafeToDelete,
        (Signature.UserOwned, _)    => BranchSafeDeleteVerdict.HoldUserOwned,
        (Signature.Unknown, MarkerState.Missing) => BranchSafeDeleteVerdict.HoldUnverified,
        _                            => BranchSafeDeleteVerdict.HoldUnverified,
    };
}

// CleanupWorktreeAsync 내부 (현 L654-672 교체)
if (branchExists && await BranchExistsAsync(branchName, ct))
{
    var verdict = await VerifySafeToDeleteAsync(branchName, taskId, ct);
    switch (verdict)
    {
        case BranchSafeDeleteVerdict.SafeToDelete:
            var (rc, msg) = await _git.RunAsync(["branch", "-D", branchName], ct: ct);
            if (rc != 0 && Directory.Exists(worktreePath))
            {
                logger.Warn($"git branch -D 실패 ({taskId}): {msg.Trim()}");
                ok = false;
            }
            break;

        case BranchSafeDeleteVerdict.NotRalphManaged:
            // 기존 메시지 유지 (사용자 브랜치)
            logger.Warn($"브랜치 '{branchName}'은 ralph가 만든 것이 아니어서 보존합니다. ...");
            break;

        case BranchSafeDeleteVerdict.HoldUserOwned:
        case BranchSafeDeleteVerdict.HoldUnverified:
            logger.Warn(
                $"브랜치 '{branchName}'은 ralph 표시는 있으나 안전 검증 실패. " +
                $"수동 삭제가 필요합니다. (이유: {verdict}) " +
                $"확인 후 'git branch -D {branchName}'으로 직접 정리하세요.");
            // ok 값은 그대로 — 디렉터리 정리는 성공했을 수 있음
            break;
    }
}
```

`enum BranchSafeDeleteVerdict { SafeToDelete, NotRalphManaged, HoldUserOwned, HoldUnverified }`
는 `WorktreeService.cs` 내부 private enum으로 선언한다(공개 API 변경 없음).

### 3.3 reflog 첫 entry 식별 방법 (C)

전략: **commit trailer 1순위, reflog 보조**.

```
1. branchName tip을 따라 reflog의 가장 오래된 entry를 얻는다:
     git reflog --no-abbrev refs/heads/{name}
   마지막 라인이 `<sha> refs/heads/{name}@{N}: branch: Created from {base}`
   형태이면 worktree add 시점의 entry → 그 sha를 후보로 사용.

2. 후보 sha의 커밋 메시지에서 trailer 검사:
     git log -1 --format=%(trailers:key=Ralph-Task-Id) <sha>
   값이 비어있지 않고 taskId와 일치하면 Signature.Ralph.
   값이 다른 task id면 → ralph가 만든 것은 맞지만 다른 task → Ralph로 인정.

3. trailer가 비어 있으면 fallback:
     git log -1 --format='%an%x09%s' <sha>
   - subject가 "guard: tasks.json을 ... 정규화"로 시작하면 Ralph (NormalizeTasksJsonAsync 산출물).
   - subject가 "merge: ... 태스크 병합"으로 시작하면 Ralph.
   - 그 외 + author가 사용자 git user.name이면 Signature.UserOwned.

4. reflog가 만료되었거나(`@{N}` 미존재) git 명령이 실패하면 Signature.Unknown.
```

신규 ralph 워크트리는 §3.4에서 첫 commit에 `Ralph-Task-Id: <id>` trailer를
무조건 박으므로 1번에서 거의 항상 끝난다. fallback은 마커 도입 이전
버전이 만든 잔여 브랜치 호환용.

> 주의 — branch reflog 첫 entry는 ralph가 worktree add 시점에 자동으로
> 만드는 entry라 항상 ralph 소유다. **trailer 검사의 진짜 타깃은 그 entry가
> 가리키는 커밋(=base 브랜치 tip이었던 시점의 base 커밋)이 아니라 그
> 이후에 ralph가 추가한 첫 커밋**이다. 따라서 실제로는 다음 식이 더
> 정확하다:
>
> ```
> firstRalphCommit = git rev-list --reverse {base}..{branchName} | head -1
> git log -1 --format=... $firstRalphCommit
> ```
>
> base 차이를 모르면 reflog 첫 entry로부터 가까운 미래 커밋을 따라간다.
> 구현 시 base ref가 있으면 그쪽을 우선 사용하고, 없으면 reflog로 fallback.

### 3.4 .ralph-marker 파일 포맷 (D)

위치: `.ralph-worktrees/{taskId}/.ralph-marker`
시점: `CreateWorktreeAsync`의 `MarkRalphManagedAsync` 직후 (`worktree add`
성공 후, 첫 커밋 발생 전).
형식: 1줄 1키 KV (UTF-8, no BOM, LF). 머신 읽기·사람 읽기 모두 가능.

```
ralph-version: 1.32
task-id: fix4-worktree-branch-guard-plan
branch: ralph/fix4-worktree-branch-guard-plan
created-at: 2026-04-30T07:14:53Z
worktree-path: /home/felix/src/ralph/.ralph-worktrees/fix4-...
host: WSL2
schema: v1
```

검증 함수 `ProbeMarkerFileAsync(taskId, branchName)`:

```
1. path = Path.Combine(_worktreeBase, taskId, ".ralph-marker")
2. 파일 부재 → MarkerState.Missing.
3. 읽기 실패 → MarkerState.Missing (best-effort, 막지 않음).
4. 파싱 후 task-id == taskId AND branch == branchName AND schema == "v1"
   → MarkerState.RalphValid.
5. 그 외(task-id 불일치 등) → MarkerState.Mismatch
   → HoldUnverified로 묶어 보수적으로 보류.
```

마커 파일은 워크트리 디렉터리에 저장되므로 워크트리가 이미 삭제된 상태
(즉 `CleanupWorktreeAsync`가 디렉터리 제거 후 브랜치 삭제 단계로 진입한
시점)에서는 항상 Missing이 된다 → 그래서 C(시그니처 trailer)가 1차 검증
역할을 맡고 D는 보강용.

> 마커 파일은 git에 커밋하지 않는다. `.gitignore`에 `.ralph-marker`를 추가
> 하지 않아도, 새 워크트리 브랜치는 add 시점에 처음부터 staged 영역이
> 비어 있고 ralph도 이 파일을 add하지 않으므로 추적되지 않는다. 만약의
> 사용자가 실수로 add하지 못하도록 워크트리 루트의 로컬
> `.git/info/exclude`에 한 줄 기록하는 옵션은 §6의 향후 작업으로 분리.

### 3.5 commit trailer 주입 지점

`Ralph/Services/GitService.cs`의 자동 커밋 (`commitMessageTemplate` 적용
경로)과 `WorktreeService.NormalizeTasksJsonAsync`의 정규화 커밋, 모두
**커밋 메시지 끝에 빈 줄 + `Ralph-Task-Id: {taskId}` trailer를 자동 부착**
한다.

```
[Task #fix4-worktree-branch-guard-plan] 안전 가드 추가

Ralph-Task-Id: fix4-worktree-branch-guard-plan
```

구현 위치(이번 plan 범위에서 변경 대상으로 식별):

- `GitService.CommitAllAsync` (또는 동급 메서드) — 메시지 끝에 trailer
  자동 append. 이미 있으면 중복 추가 안 함 (`git interpret-trailers --if-exists addIfDifferent --trailer`로 위임 가능).
- `WorktreeService.NormalizeTasksJsonAsync` — `git commit -m`을
  trailer 포함하도록 변경.

trailer는 사용자 커밋 메시지의 일부가 아니라 ralph 표식이므로, ralph가
직접 만드는 모든 worktree-내 커밋에만 부착하고 사용자가 worktree 안에서
직접 만든 커밋(거의 없지만)에는 부착되지 않는다 → C 시그니처 검증의
정확도 핵심 근거.

---

## 4. 사용자 안내 메시지 (한국어)

`logger.Warn` 및 `AnsiConsole`(가능한 경우 stderr) 양쪽으로 동일 문구 송출.

| 케이스 | 메시지 |
|---|---|
| Hold (사용자 시그니처) | `브랜치 '{name}'은 ralph 표시는 있으나 reflog/커밋이 사용자 소유로 식별되어 삭제를 보류합니다. 직접 만든 브랜치라면 'git config --unset branch.{name}.ralphManaged' 후 그대로 두세요. ralph 잔여물이라면 'git branch -D {name}'으로 수동 정리하세요.` |
| Hold (식별 불가) | `브랜치 '{name}'은 ralph 표시는 있으나 안전 검증 실패(reflog 만료 또는 .ralph-marker 부재). 수동 삭제가 필요합니다 — 'git log {name} -1' 결과를 확인하고 ralph가 만든 것이 맞다면 'git branch -D {name}'으로 정리하세요.` |
| 정상 보존 (config·worktree 둘 다 없음) | (기존 메시지 유지) `브랜치 '{name}'은 ralph가 만든 것이 아니어서 보존합니다. ...` |

`fix2.md` 요구사항의 표준 문구
> "브랜치 X는 ralph 표시는 있으나 안전 검증 실패. 수동 삭제 필요"

는 위 두 Hold 케이스의 머리말로 그대로 사용한다.

---

## 5. 회귀 / 호환성

| 시나리오 | 기대 |
|---|---|
| **정상 ralph 워크트리 라이프사이클** (create → run → cleanup) | trailer + 마커 모두 박혀 있음 → SafeToDelete. **회귀 없음**. |
| **마커 도입 이전 버전이 남긴 ralph 브랜치 + 워크트리 디렉터리 잔존** | A(config) 또는 B(worktree list)는 통과, D(마커) Missing. C는 reflog/trailer 부재로 Unknown. → HoldUnverified. **사용자 1회 수동 정리 필요**. 데이터 손실 방향이 아니므로 수용. 마이그레이션 안내는 §7. |
| **마커 이전 버전 + 워크트리 디렉터리 이미 사라짐 + reflog 살아있음** | C가 Unknown(트레일러 없음, 메시지 fallback 사용) → 메시지 fallback이 ralph 패턴(`merge:` / `guard:` 시작) 매칭 시 Ralph로 인정 → SafeToDelete. 일반 케이스 회귀 거의 없음. |
| **사용자 ralph/foo 브랜치 (config 없음)** | A=false, B=false → NotRalphManaged. **이미 보존**. |
| **사용자 ralph/foo + ralphManaged config가 외부 도구로 잘못 박힘** | A=true, C=UserOwned (사용자 author + 사용자 메시지) → HoldUserOwned. **삭제 차단**. fix2 핵심 시나리오. |
| **fresh clone 후 ralph 미실행** | config 없음, 마커 없음 → A=false, B=false → NotRalphManaged. 변화 없음. |
| **CreateWorktreeAsync에서 동명 브랜치 발견** | 같은 가드 적용. 사용자 브랜치는 던지는 InvalidOperationException 그대로, 단 메시지에 "안전 검증 실패: {이유}" 추가. |

---

## 6. 테스트 시나리오 (Ralph.Tests/WorktreeServiceTests.cs)

각 시나리오는 임시 git repo + 임시 `.ralph-worktrees/` 베이스로 격리.

1. **safe_delete_normal_lifecycle**
   - CreateWorktreeAsync → 워크트리에 commit (ralph가 메시지+trailer 부착) →
     CleanupWorktreeAsync → 브랜치/디렉터리 모두 정리됨.
   - assert: `BranchExistsAsync` false, 디렉터리 부재.

2. **hold_when_user_owned_branch_with_stale_config**
   - 외부에서 `git branch ralph/test`로 사용자 브랜치 생성 + 사용자 author로 commit.
   - `git config branch.ralph/test.ralphManaged true` 강제 박음.
   - CleanupWorktreeAsync 호출.
   - assert: 브랜치 존재 유지, logger에 "안전 검증 실패" 문구, ok=true (디렉터리는 없으니).

3. **hold_when_unverifiable_legacy_branch**
   - ralph 워크트리 add 후 `.ralph-marker` 강제 삭제 + commit trailer 없이
     (legacy 시뮬레이션) 일반 커밋만 1개.
   - `git reflog expire --expire=now --all`로 reflog까지 비움.
   - CleanupWorktreeAsync.
   - assert: HoldUnverified 메시지, 브랜치 보존.

4. **safe_delete_when_marker_missing_but_trailer_present**
   - ralph 워크트리 + commit (trailer 있음). 마커 파일만 삭제.
   - CleanupWorktreeAsync.
   - assert: SafeToDelete (C 통과). 회귀 안전망.

5. **config_present_no_worktree_directory**
   - ralph가 만든 브랜치 + trailer commit, 그러나 워크트리 디렉터리는 외부에서
     이미 삭제됨 (`rm -rf` 시뮬레이션).
   - CleanupWorktreeAsync.
   - assert: A=true, B=false, C=Ralph → SafeToDelete. fix2.md 검증 케이스 1.

6. **user_branch_no_config**
   - 사용자가 `git branch ralph/test` + 임의 commit, config 없음.
   - CleanupWorktreeAsync.
   - assert: NotRalphManaged 경로, 기존 보존 메시지. fix2.md 검증 케이스 2.

7. **marker_file_format_round_trip** (단위 테스트)
   - `WriteMarker(..)` 후 `ProbeMarkerFileAsync(..)`이 RalphValid 반환.
   - task-id 변조 시 Mismatch 반환.

8. **trailer_extraction_from_commit_message** (단위 테스트, GitService)
   - 메시지에 이미 `Ralph-Task-Id:` 있으면 중복 추가 안 됨(`git interpret-trailers`).

---

## 7. 마이그레이션 / 향후 작업

- **첫 실행 자동 보강**: `IsRalphManagedBranchAsync`가 legacy fallback(B)으로
  통과하는 순간 이미 마커를 박는 동작이 존재(L580). 동일하게 worktree
  디렉터리가 살아 있다면 `WriteMarkerAsync`도 그 경로에서 호출 →
  legacy 워크트리도 점차 신규 가드를 만족.
- **HoldUnverified 통계**: `.ralph-logs/`에 1줄 append (`branch-guard.jsonl`)
  하여 사용자가 어떤 브랜치를 수동 정리해야 하는지 사후 조회 가능.
- **WorktreeCleanupCommand UX**: 보류된 브랜치를 모아 한 번에
  `--force-prune` 옵션으로 정리하는 흐름은 별도 fix 항목으로 분리(범위
  외).
- **`.git/info/exclude`에 `.ralph-marker` 자동 추가**: 마커가 사용자
  실수로 staged되지 않도록 워크트리 생성 직후 한 줄 기록.

---

## 8. 영향 파일 (구현 단계 예상)

- `Ralph/Services/WorktreeService.cs` — `IsRalphManagedBranchAsync` 유지,
  `VerifySafeToDeleteAsync` / `ProbeRalphSignatureAsync` /
  `ProbeMarkerFileAsync` / `WriteMarkerAsync` 신설. `CleanupWorktreeAsync` /
  `CreateWorktreeAsync`에서 호출.
- `Ralph/Services/GitService.cs` — 자동 커밋 메시지에 `Ralph-Task-Id` trailer
  자동 부착(중복 방지).
- `Ralph/Services/RalphPaths.cs` — `MarkerFileName = ".ralph-marker"`,
  `TrailerKey = "Ralph-Task-Id"` 상수.
- `Ralph.Tests/WorktreeServiceTests.cs` — §6 시나리오 추가.
- `Ralph.Tests/GitServiceTests.cs`(존재 시) — trailer 자동 부착 테스트.

설계 문서 범위는 여기까지. 실제 코드 변경은 본 plan 승인 후 별도
implementation 태스크에서 진행.

---

## 9. 완료 보고

- **생성**: `docs/fix2/04-worktree-branch-guard-plan.md` (본 문서)
- **수정**: 없음
- **Scope 외 변경**: 없음
