namespace Ralph.Services;

/// <summary>
/// `--run` 파이프(<see cref="ParallelExecutor"/> → <see cref="MergeOrchestrator"/>)에 흘러
/// 내려가는 사용자 옵션(CLI flag / env / workflow.tasks.json 병합 결과)을 한 덩어리로 묶은
/// 값 객체. 새 옵션이 추가될 때 두 클래스 생성자와 모든 호출자를 함께 수정하던 부담을
/// 제거하기 위해 도입했다.
///
/// 정책:
/// - 객체 그래프 의존성(TaskManager, GitService, ...)은 여기 넣지 않는다 — 생성자 파라미터로 유지.
/// - 단일 실행 동안 변하지 않는 "값"만 담는다. 진행 상태(<c>BudgetGate.Reached</c> 등)는 별도.
/// </summary>
public sealed record RunOptions(
    string TasksFile,
    string? ModelOverride = null,
    bool StrictFiles = false,
    double? BudgetUsd = null,
    bool SharedWorktrees = false,
    bool NoSmokeTest = false,
    string? SmokeTestCommandOverride = null,
    bool AutoRollbackOnSmokeFail = false);
