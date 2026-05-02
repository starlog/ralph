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

        // 테스트 태스크 전용: 워크트리 환경 주의사항
        if (task.Category is "testing")
        {
            sb.AppendLine("## 워크트리 실행 환경 주의사항 (테스트 태스크)");
            sb.AppendLine("이 태스크는 `.ralph-worktrees/{taskId}/` git worktree 안에서 실행됩니다. 메인 레포 루트에 있는 `node_modules/`는 worktree 루트의 **상위**가 아닌 **형제** 디렉토리이므로, Vite 기본값인 `server.fs.strict: true`가 setupFile(예: `@testing-library/jest-dom/vitest`) 로드를 차단할 수 있습니다.");
            sb.AppendLine();
            sb.AppendLine("**vitest / vite 기반 프론트엔드 테스트라면 다음을 확인하세요:**");
            sb.AppendLine("1. 테스트 실행 전 `npx vitest run --reporter=verbose 2>&1 | head -20` 으로 fs.strict 오류 여부를 먼저 확인하세요.");
            sb.AppendLine("2. `Failed to load url` / `ENOENT` / `Access denied to` 오류가 나오면 `vitest.config.ts` (또는 `vite.config.ts`)의 `test:` 블록에 `server: { fs: { strict: false } }` 를 추가하세요.");
            sb.AppendLine("3. `vitest.config.ts`가 이 태스크의 Scope(modifiedFiles)에 포함되어 있다면 수정해도 됩니다. **Scope에 없으면 수정하지 말고 완료 보고에 명시하세요** (pre-rebase cleanup이 미선언 변경을 폐기하므로 fix가 반영되지 않습니다).");
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
