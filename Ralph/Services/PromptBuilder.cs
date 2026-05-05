using System.Text;
using Ralph.Models;

namespace Ralph.Services;

/// <summary>
/// Claude에 전달될 task 실행 prompt를 조립합니다.
/// task.Prompt 원문에 Scope, 의존 산출물, sibling, 절대 금지 사항 등 컨텍스트를 추가합니다.
/// </summary>
public static class PromptBuilder
{
    public static string Build(
        TaskItem task,
        TaskManager taskManager,
        string tasksFile,
        IReadOnlyList<TaskItem>? siblings = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Task ID: {task.Id}");
        sb.AppendLine($"Title: {task.Title}");
        sb.AppendLine($"Phase: {task.Phase ?? "-"} | Category: {task.Category ?? "-"}");
        sb.AppendLine();

        // Scope: modifiedFiles / outputFiles 경계 명시
        var hasScope = (task.ModifiedFiles is { Count: > 0 }) || (task.OutputFiles is { Count: > 0 });
        if (hasScope)
        {
            sb.AppendLine("## Scope (반드시 준수)");
            if (task.ModifiedFiles is { Count: > 0 })
            {
                sb.AppendLine("이 태스크에서 수정/생성 가능한 파일:");
                foreach (var f in task.ModifiedFiles)
                    sb.AppendLine($"  - {f}");
            }
            if (task.OutputFiles is { Count: > 0 })
            {
                sb.AppendLine("이 태스크가 생성해야 할 산출물:");
                foreach (var f in task.OutputFiles)
                    sb.AppendLine($"  - {f}");
            }
            sb.AppendLine("위 목록 외의 파일은 변경하지 마세요. 부득이하게 추가로 수정해야 한다면 완료 보고에 명시하세요.");
            sb.AppendLine();
        }

        // 의존 태스크의 산출물
        if (task.DependsOn is { Count: > 0 })
        {
            sb.AppendLine("## 의존 태스크 산출물 (참고)");
            foreach (var depId in task.DependsOn)
            {
                var dep = taskManager.GetTask(depId);
                if (dep == null) continue;
                var depFiles = new List<string>();
                if (dep.OutputFiles is { Count: > 0 }) depFiles.AddRange(dep.OutputFiles);
                if (dep.ModifiedFiles is { Count: > 0 }) depFiles.AddRange(dep.ModifiedFiles);
                if (depFiles.Count > 0)
                    sb.AppendLine($"  - {dep.Id} ({dep.Title}): {string.Join(", ", depFiles.Distinct())}");
                else
                    sb.AppendLine($"  - {dep.Id} ({dep.Title})");
            }
            sb.AppendLine();
        }

        // 같은 batch에서 병렬 실행 중인 sibling
        if (siblings is { Count: > 0 })
        {
            sb.AppendLine("## 동시 실행 중인 다른 태스크 (별도 worktree)");
            foreach (var s in siblings)
            {
                var files = s.ModifiedFiles is { Count: > 0 }
                    ? string.Join(", ", s.ModifiedFiles)
                    : "(미명시)";
                sb.AppendLine($"  - {s.Id}: {s.Title} | modifiedFiles: {files}");
            }
            sb.AppendLine("위 태스크들과 병렬 실행 중이므로 자신의 Scope 외 파일을 절대 건드리지 마세요. 머지 시 충돌이 발생합니다.");
            sb.AppendLine();
        }

        // 테스트 태스크 전용: verification 범위 안내
        if (task.Category is "testing")
        {
            sb.AppendLine("## 테스트 태스크 안내");
            sb.AppendLine("이 태스크는 테스트 코드를 **작성**하는 단계입니다. 전체 테스트 스위트 실행은 머지 후 `workflow.smokeTest`가 격리 worktree(`.ralph-smoke`)에서 수행하므로, 본 태스크의 `verification.command`는 작성한 테스트 파일의 **컴파일/타입 체크만** 수행해야 합니다 (예: `tsc --noEmit tests/X.test.ts`, `python -m py_compile tests/test_x.py`).");
            sb.AppendLine("worktree 안에서 `npx vitest run` / `npm test` / `pytest tests/` 같은 실제 실행 명령을 verification으로 사용하지 마세요 — sibling 미머지 import 실패와 vite `server.fs.strict` 차단으로 false-failure가 빈번합니다 (이런 시나리오는 smoke 단계가 정상 처리합니다).");
            sb.AppendLine();
        }

        // 절대 금지
        sb.AppendLine("## 절대 금지 사항");
        sb.AppendLine($"- `{tasksFile}` 수정 금지 (worktree 격리 환경, 변경 시 머지 충돌이 발생합니다)");
        sb.AppendLine("- 민감 파일(.env, .env.*, *.pem, *.key, *.p12, *.pfx, credentials.json, service-account*.json, id_rsa, id_ed25519) 생성/커밋 금지");
        sb.AppendLine("- 위에 명시한 Scope 외 파일 변경 금지 (필요 시 보고)");
        sb.AppendLine();

        // 작업 지시
        sb.AppendLine("## 작업 지시");
        if (string.IsNullOrWhiteSpace(task.Prompt))
            throw new InvalidOperationException(
                $"task '{task.Id}'의 prompt가 비어있습니다. " +
                $"silent 마스킹 대신 명시적 실패 — `ralph --validate`로 plan을 점검하세요.");
        sb.AppendLine(task.Prompt);
        sb.AppendLine();

        // 완료 보고
        sb.AppendLine("## 완료 시 보고");
        sb.AppendLine("- 실제로 생성/수정한 파일 목록 (전체 경로)");
        sb.AppendLine("- Scope 외 파일을 건드렸다면 그 사유와 파일");
        sb.AppendLine($"- 추가 컨텍스트는 `{tasksFile}`의 apiSpecs, samplePages 등에서 확인 가능");

        return sb.ToString();
    }
}
