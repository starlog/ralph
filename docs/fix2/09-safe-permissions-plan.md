# Fix2 #9 — `--dangerously-skip-permissions` 사용 명시화 + `--safe-permissions` 옵트아웃 설계

## 1. 배경

`Ralph/Services/ClaudeService.cs:165` 의 `RunStreamAsync` 가 모든 Claude 호출에
`--dangerously-skip-permissions` 를 무조건 부착한다. 워크트리 격리가 있다 해도
워크트리는 호스트 파일시스템과 동일 권한으로 동작하므로, 사실상 Claude 가 호스트
FS 전체에 접근 가능하다.

현재 README 보안 노트(`README.md:38-42`, `README.en.md:40-44`)는 한 단락으로
경고는 되어 있지만:

- "항상 이 플래그가 켜진다" 는 **명시적인 진술**이 약하다.
- 옵트아웃 수단이 없어 "한 번만 안전하게 plan 만들고 검토하고 싶다" 같은 합리적
  요구를 수용할 수 없다.
- 환경변수로 강제 safe 모드를 거는 정책 hook 도 없다 (조직/CI 차원의 가드 부재).

본 fix 는 다음을 한 PR 로 정리한다.

1. README 한/영 보안 섹션 강화 (현재 동작 명시 + 운영 권장사항).
2. CLI 옵션 `--safe-permissions` 추가 — `--dangerously-skip-permissions` 를 부착하지
   않는 표준 모드 옵트아웃.
3. 환경변수 `RALPH_REQUIRE_PERMISSIONS=true` — 조직 차원 강제 safe 모드.
4. 자동화 흐름(`--run`, parallel, interactive)에서 safe 모드를 만났을 때의 정책
   (경고 + 진행 vs 차단) 결정.

Scope: 본 태스크는 **설계 문서만** 작성한다. 실제 구현/테스트는 후속 impl/test
태스크가 담당한다.

---

## 2. 조사 결과

### 2.1 Claude 호출 args 빌드 위치

`Ralph/Services/ClaudeService.cs:148-184` 에 `ProcessStartInfo` 를 만들고 다음
순서로 `ArgumentList` 를 채운다.

```text
-p
--dangerously-skip-permissions   ← (165 줄, 무조건 부착)
--output-format stream-json
--include-partial-messages
--verbose
[--model <model>]
[--allowedTools <tools>]
```

이 메서드는 `ClaudeService` 외부 진입점이다 — 모든 Claude 호출(plan, run,
verify-self-fix, llm-critique 등)이 이 한 곳을 통과한다. 즉, **분기 한 군데만**
넣으면 전 시스템에 일관되게 적용된다.

`ClaudeService` 인스턴스 생성은 `Program.cs` 또는 각 Command 의 컨텍스트 빌드
지점(`CommandContext`)에서 이루어진다. `CommandContext` 가 이미 `--strict-files`,
`--no-smoke-test` 등 boolean 플래그를 보유하므로 같은 패턴으로 추가한다.

### 2.2 ArgParser 옵션 추가 패턴

`Ralph/Commands/ArgParser.cs:50-58` 의 boolean flag 처리 블록은 단순 `argList.Remove`:

```csharp
var debug = argList.Remove("--debug");
var sequential = argList.Remove("--sequential");
var forceFlag = argList.Remove("--force");
var cliStrictFiles = argList.Remove("--strict-files");
var cliSharedWorktrees = argList.Remove("--shared-worktrees");
var cliNoSmokeTest = argList.Remove("--no-smoke-test");
var llmCritique = argList.Remove("--llm-critique");
```

env var 처리는 `ArgParser.cs:25-48` 에 모여 있다. 같은 자리에 한 줄 추가:

```csharp
var envRequirePermissions = string.Equals(
    Environment.GetEnvironmentVariable("RALPH_REQUIRE_PERMISSIONS"), "true",
    StringComparison.OrdinalIgnoreCase);
```

`CommandContext` POCO 에는 `CliSafePermissions`, `EnvRequirePermissions` 두
필드를 추가한다.

### 2.3 README 보안 섹션 (현재 vs 보강안)

현재 `README.md:38-42`:

```markdown
## ⚠️ 보안 주의 — 먼저 읽어 주세요

Ralph는 호스트 머신에서 Claude Code를 `--dangerously-skip-permissions`로
직접 실행합니다. 즉, **Claude가 여러분의 컴퓨터에 있는 파일을 자유롭게
읽고 쓸 수 있습니다** — `.env`, SSH 키, AWS 자격증명 등이 노출될 수 있습니다.

본인이 작성한/이해하는 PRD라면 일반 개발 환경에서 써도 괜찮지만, **남이 준
PRD나 `tasks.json`** 은 반드시 별도 사용자 계정 / VM / 컨테이너에서 돌리세요.
```

문제점: (1) "항상" 이 명시되어 있지 않음, (2) 옵트아웃 존재가 안 알려짐,
(3) 환경변수 정책 hook 부재.

---

## 3. 설계

### 3.1 우선순위 (확정)

`safe = (CLI --safe-permissions) || (env RALPH_REQUIRE_PERMISSIONS=true)`

- **CLI `--safe-permissions`** 가 가장 우선.
- 그 다음 **env `RALPH_REQUIRE_PERMISSIONS=true`** 가 강제 safe 모드 (env 가
  true 면 CLI 에서 명시적으로 끄는 수단은 제공하지 않는다 — 조직 차원 가드 의도).
- 둘 다 false/unset 이면 **현행 동작 유지** (`--dangerously-skip-permissions` 부착).

회귀 위험을 막기 위해 **기본값은 dangerously-skip** 이다. 이는 fix2.md #9 의
"기본 동작 변동 없음" 조건과 일치한다.

CLI flag 와 env 의 의미가 동일하지만 우선순위는 명확하다 — env 는 조직/CI
차원 강제이므로 사용자가 끌 수 없게 만든다. (만약 env override 가 필요하면
후속 작업에서 `RALPH_REQUIRE_PERMISSIONS=false` 의미를 추가하되, 본 fix 의
범위 밖.)

### 3.2 `ClaudeService` args 분기 의사코드

```csharp
public class ClaudeService(int maxRetries = 2, int retryDelay = 5) : IAgentRunner
{
    /// <summary>
    /// true 면 --dangerously-skip-permissions 를 부착하지 않는다 (Claude 가 권한 요청 prompt 표시).
    /// 자동화 환경에서는 비실용적이지만 일회성 plan / 수동 검토 시 유용.
    /// 기본 false (현행 동작).
    /// </summary>
    public bool SafePermissions { get; set; }

    public virtual async Task<ClaudeResult> RunStreamAsync(...)
    {
        // ...
        psi.ArgumentList.Add("-p");
        if (!SafePermissions)
            psi.ArgumentList.Add("--dangerously-skip-permissions");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        // ...
    }
}
```

`ClaudeService` 를 만드는 모든 진입점 (Program.cs / Command 들) 에서 다음과 같이
세팅:

```csharp
var safe = ctx.CliSafePermissions || ctx.EnvRequirePermissions;
var claude = new ClaudeService(maxRetries, retryDelay) { SafePermissions = safe };
```

### 3.3 ArgParser 변경

```csharp
// boolean flag 블록에 추가
var cliSafePermissions = argList.Remove("--safe-permissions");

// env 블록에 추가
var envRequirePermissions = string.Equals(
    Environment.GetEnvironmentVariable("RALPH_REQUIRE_PERMISSIONS"), "true",
    StringComparison.OrdinalIgnoreCase);

// CommandContext 에 두 값 전달
return new CommandContext
{
    // ...기존 필드...
    CliSafePermissions = cliSafePermissions,
    EnvRequirePermissions = envRequirePermissions,
};
```

`CommandContext` POCO 에 두 bool 필드 추가.

### 3.4 자동화 흐름에서 safe 모드 정책

safe 모드는 Claude 가 모든 tool call 마다 권한 prompt 를 띄우는데, ralph 의
자동화 흐름은 stdin/stdout 을 파이프로 잡고 있어 사용자 응답이 사실상 불가능하다.
따라서 **자동화 흐름에서 safe 모드는 효과적으로 hang 을 유발한다**.

다음 정책으로 분기한다.

| 명령 | safe 모드 시 동작 |
|---|---|
| `--plan`, `--plan-prompt`, `--validate`, `--critique`, `--list`, `--graph`, `--logs`, `--cost`, `--status` | 그대로 진행 (plan 은 단일 호출이므로 권한 prompt 가 사람이 응답 가능한 환경에서 의미 있음) |
| `--show-prompt` | 영향 없음 (Claude 호출 안 함) |
| `--run`, `--task`, `--dry-run` | 콘솔에 **경고** 출력 후 진행. 단, 다음 두 조건 중 하나라도면 **차단** + exit 1: |
| └ parallel 실행 (`workflow.parallel.enabled=true` && batch >= 1 task 가 동시) | 차단 (하나 이상의 worker 가 응답 가능한 stdin 을 갖지 못함) |
| └ TTY 미연결 (`Console.IsInputRedirected` true) | 차단 (CI/파이프 환경 — 권한 prompt 응답 수단 없음) |
| `--interactive` | 진행 (사용자가 각 task 사이에 개입하므로 권한 prompt 도 응답 가능) |

차단 시 메시지 (한국어):

```
safe-permissions 모드는 자동화 실행과 호환되지 않습니다.
이유: <parallel 실행 / TTY 미연결>.
- plan 만 만들고 싶다면 `ralph --plan PRD.md --safe-permissions` 를 사용하세요.
- 실제 실행은 safe 모드를 끄거나 `--interactive` 로 전환하세요.
```

차단 결정은 `RunCommand` / `SingleTaskCommand` / `DryRunCommand` 진입 직후
`CommandContext` 를 본 한 곳에서 수행한다 (`ParallelExecutor` 까지 들어가기 전).

env `RALPH_REQUIRE_PERMISSIONS=true` 인데 자동화 명령이 들어왔을 때도 같은
차단을 적용한다 — env 는 조직 차원 강제이므로 사용자가 우회하려고 `--run` 을
호출했다면 그 의도 자체가 정책 위반이다.

### 3.5 README 보강안 (한/영)

`README.md` 38-44 줄을 다음으로 교체:

```markdown
## ⚠️ 보안 주의 — 먼저 읽어 주세요

Ralph는 **항상** 호스트 머신에서 Claude Code를 `--dangerously-skip-permissions`
플래그와 함께 실행합니다 (`--safe-permissions` 또는 `RALPH_REQUIRE_PERMISSIONS=true`
로 옵트아웃 가능, 아래 참고). 이는 다음을 의미합니다:

- Claude가 권한 요청 prompt 없이 **호스트 파일시스템 전체를 읽고 쓸 수 있습니다**.
- 워크트리(`.ralph-worktrees/`)가 격리 디렉토리로 보이지만, **OS 권한 차원에서는
  격리가 아닙니다** — `.env`, `~/.ssh`, `~/.aws/credentials` 모두 접근 가능합니다.
- 자동화 흐름(`--run`, `--task`)은 prompt 응답 수단이 없어 권한 요청 모드와
  근본적으로 호환되지 않습니다.

### 운영 권장 사항

| 상황 | 권장 |
|---|---|
| 본인이 작성/검토한 PRD | 일반 개발 환경 OK |
| 외부 PRD / `tasks.json` | 별도 사용자 계정, VM, 컨테이너에서 실행 |
| 민감 환경 (production-adjacent, secrets 보유) | 격리된 VM/컨테이너 + 최소 권한 사용자 |
| 일회성 plan 검토 | `ralph --plan PRD.md --safe-permissions` (Claude 가 각 tool 호출 전 사용자 승인 요청) |

### 옵트아웃 옵션

- **`--safe-permissions`** — 이번 명령에 한해 표준 권한 모드 사용. 자동화 명령
  (`--run`/`--task`/`--dry-run`)에서는 차단됩니다 — `--plan` / `--interactive`
  에만 의미가 있습니다.
- **`RALPH_REQUIRE_PERMISSIONS=true`** — 조직/CI 차원에서 강제. 모든 명령이
  safe 모드로 동작하며, 자동화 명령은 위와 동일하게 차단됩니다.
```

`README.en.md` 40-46 줄도 동일한 의미로 교체 (구현 태스크에서 동기 작성).

---

## 4. 구현 변경 요약

| 파일 | 변경 |
|---|---|
| `Ralph/Services/ClaudeService.cs` | `SafePermissions` public 프로퍼티 추가. `RunStreamAsync` 의 `--dangerously-skip-permissions` 부착을 `if (!SafePermissions)` 로 감쌈. |
| `Ralph/Commands/ArgParser.cs` | `--safe-permissions` boolean flag 파싱, `RALPH_REQUIRE_PERMISSIONS` env 파싱, `CommandContext` 로 전달. |
| `Ralph/Commands/CommandContext.cs` | `CliSafePermissions`, `EnvRequirePermissions` 두 bool 필드 추가. |
| `Ralph/Commands/RunCommand.cs` | safe 모드 + 자동화 차단 정책 게이트. parallel 실행 / TTY 미연결 시 exit 1 + 안내 메시지. |
| `Ralph/Commands/SingleTaskCommand.cs` | 같은 게이트 (TTY 미연결만 차단, parallel 은 무관). |
| `Ralph/Commands/DryRunCommand.cs` | 같은 게이트 (parallel 시뮬 시 차단 — 사실상 ParallelExecutor 가 호출되지 않으므로 TTY 만 검사해도 충분, 결정은 impl 단계에서). |
| `Ralph/Commands/PlanCommand.cs` 등 plan 계열 | safe 플래그를 `ClaudeService.SafePermissions` 로 전달만 하고 차단 게이트 없음. |
| `Ralph/Commands/HelpCommand.cs` (또는 그에 상응하는 help 출력) | `--safe-permissions` 플래그 항목 추가 + `RALPH_REQUIRE_PERMISSIONS` 환경변수 항목 추가. |
| `README.md`, `README.en.md` | 보안 섹션 위 안에 따라 교체. |
| `CLAUDE.md` (envvars 표) | `RALPH_REQUIRE_PERMISSIONS` 한 줄 추가, 옵션 표에 `--safe-permissions` 한 줄 추가. |

명시적으로 **건드리지 않을** 파일:

- `pricing.json`, `ralph-schema.json` — 스키마 변경 없음.
- `tasks.json` — 워크플로우 설정으로 노출하지 않는다 (이번 옵션은 정책 차원
  knob 이지 plan 산출물의 속성이 아니므로 schema 에 추가하지 않는다).

---

## 5. 회귀 / 호환성

- **기본 동작 변동 없음.** `--safe-permissions` 미지정 + env unset 이면 현재와
  동일하게 `--dangerously-skip-permissions` 가 부착된다.
- 기존 모든 테스트가 그대로 통과해야 한다 — `ClaudeService` 의 args 구성 검증
  테스트 (만약 있다면) 도 default path 에서 동일.
- `--safe-permissions` 가 자동화 명령에서 차단되는 부분은 **신규 동작**이지만,
  새 옵션이므로 기존 사용자에게 영향 없음.
- env `RALPH_REQUIRE_PERMISSIONS=true` 는 새 변수 — 기본 unset 이므로 기존
  사용자에게 영향 없음.

---

## 6. 테스트 시나리오

구현 단계에서 다음 단위/통합 테스트를 추가한다.

### 6.1 ClaudeService args 구성 (단위)

`Ralph.Tests/ClaudeServiceArgsTests.cs` (신규):

1. **default path** — `SafePermissions = false` (생성자 직후 기본값) 일 때 args
   에 `--dangerously-skip-permissions` 가 포함된다.
2. **safe path** — `SafePermissions = true` 일 때 args 에서 `--dangerously-skip-permissions`
   가 제거된다.
3. 다른 args (`--output-format stream-json`, `--include-partial-messages`,
   `--verbose`, `--model <m>`, `--allowedTools <t>`) 는 두 path 에서 동일.

검증 방식: `RunStreamAsync` 를 직접 부르지 않고 args 빌드 부분을 internal helper
로 분리해 단위 테스트 (또는 `Process.Start` 를 mocking 가능하게 분리). impl 단계
에서 작은 helper `BuildArgumentList(model, allowedTools, safe)` 를 추출하는
리팩터를 함께 수행한다.

### 6.2 ArgParser 통합 (단위)

`Ralph.Tests/ArgParserSafePermissionsTests.cs` (신규):

1. `--safe-permissions` 단독 → `CliSafePermissions = true`.
2. `--safe-permissions` 미지정 → `CliSafePermissions = false`.
3. env `RALPH_REQUIRE_PERMISSIONS=true` → `EnvRequirePermissions = true`
   (다른 case-variant `True`/`TRUE` 도 인식).
4. env unset / 임의 값 → `EnvRequirePermissions = false`.
5. CLI 와 env 동시 설정 시 두 필드 모두 true (우선순위 결합은 진입 명령에서
   `||` 으로 처리하므로 ArgParser 자체는 둘 다 그대로 전달).

### 6.3 자동화 차단 게이트 (통합)

`Ralph.Tests/RunCommandSafePermissionsTests.cs` (신규):

1. `--run --safe-permissions` + parallel 활성 → exit 1 + stderr 에 한국어 안내
   문자열 일부 포함 (`"safe-permissions 모드는 자동화 실행과 호환되지 않습니다"`).
2. `--run --safe-permissions` + `--sequential` + `Console.IsInputRedirected=true`
   (테스트에서 stdin redirect) → 차단.
3. `--run --safe-permissions` + `--sequential` + TTY → 진행 (Claude 는 fake
   IAgentRunner 로 mock).
4. env `RALPH_REQUIRE_PERMISSIONS=true` 만 설정 + `--run` → 위와 동일하게 차단.
5. `--plan PRD.md --safe-permissions` → 차단 없이 진행.

### 6.4 README diff 리뷰 (사람)

코드 테스트로 검증할 수 없으므로 PR review 체크리스트에 명시:

- 한/영 README 보안 섹션이 위 3.5 의 의미를 모두 담는가?
- `--safe-permissions` 와 `RALPH_REQUIRE_PERMISSIONS` 가 둘 다 언급되는가?
- 기본 동작이 dangerously-skip 임이 명확히 적혀 있는가?

---

## 7. 작업 분할 제안 (후속 태스크용)

본 fix 는 다음 3 태스크로 분할 권장.

1. **fix2-9-impl** — `ClaudeService.SafePermissions`, `ArgParser`,
   `CommandContext`, 자동화 차단 게이트, `HelpCommand` / `CLAUDE.md` 업데이트.
2. **fix2-9-test** — 6.1 / 6.2 / 6.3 의 단위·통합 테스트.
3. **fix2-9-docs** — `README.md` / `README.en.md` 보안 섹션 교체. (impl 과 동일
   PR 으로 묶어도 OK — 분리 이유는 leaf-only 변경 분리해 리뷰 부담 줄이기.)

---

## 8. 미해결 / 후속 검토 사항

- **env override 의미 확장** — `RALPH_REQUIRE_PERMISSIONS=false` 로 명시적
  비활성화를 인정해야 하는가? 현재 안은 "true 일 때만 의미 있음" — 만약 사용자
  shell 에 부주의하게 `RALPH_REQUIRE_PERMISSIONS=false` 가 설정되어 있을 때
  무시되는 게 안전 default. (변경 시 fix2 #9 의 후속 작업으로 분리.)
- **Claude CLI 권한 prompt 의 실제 인터랙션 형태** — Claude Code 가 stdin 으로
  응답을 받는지, TTY 직접 attach 가 필요한지에 따라 `--interactive` 모드의
  실효성이 달라질 수 있다. impl 단계에서 실제로 한 번 띄워보고 확인 필요.
- **--allowedTools 와의 상호작용** — safe 모드에서 `--allowedTools` 를 함께
  주면 Claude 가 어떤 동작을 하는가? 본 fix 범위 밖이지만, `--plan` 은 이미
  `--allowedTools` 를 비워두므로 영향 없음.
