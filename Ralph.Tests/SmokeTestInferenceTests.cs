using Ralph.Models;
using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

/// <summary>
/// SmokeTestPlanner의 우선순위 / 마커 추론 / 패키지 매니저 감지 / monorepo 인식 / docs-only 스킵
/// 동작을 격리된 임시 디렉토리에서 검증한다. 실제 명령은 실행하지 않는다 (순수 함수).
/// </summary>
public class SmokeTestPlannerTests : IDisposable
{
    private readonly string _root;

    public SmokeTestPlannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ralph-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ─── 마커 기반 추론 ────────────────────────────────────────────────────────

    [Fact]
    public void Csproj_marker_infers_dotnet_build()
    {
        File.WriteAllText(Path.Combine(_root, "Foo.csproj"), "<Project />");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Contains("dotnet build", spec!.Command);
        Assert.Equal(180, spec.TimeoutSec);
    }

    [Fact]
    public void Sln_marker_infers_dotnet_build()
    {
        File.WriteAllText(Path.Combine(_root, "Foo.sln"), "");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Contains("dotnet build", spec!.Command);
    }

    [Fact]
    public void CargoToml_marker_infers_cargo_check()
    {
        // build → check로 변경됨 (더 빠르고 type/borrow 검증은 동일).
        File.WriteAllText(Path.Combine(_root, "Cargo.toml"), "[package]");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Contains("cargo check", spec!.Command);
        Assert.DoesNotContain("cargo build", spec.Command);
    }

    [Fact]
    public void GoMod_marker_infers_go_build()
    {
        File.WriteAllText(Path.Combine(_root, "go.mod"), "module foo");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Contains("go build", spec!.Command);
    }

    [Fact]
    public void Pyproject_marker_infers_python_compileall()
    {
        File.WriteAllText(Path.Combine(_root, "pyproject.toml"), "[project]");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Contains("compileall", spec!.Command);
        Assert.Equal(120, spec.TimeoutSec);
    }

    [Fact]
    public void Python_inference_uses_host_appropriate_interpreter_command()
    {
        // Windows에서는 `python3.exe`가 Microsoft Store 스텁(exit 9009)일 확률이 높아 `python`을 써야 한다.
        // POSIX에서는 시스템 `python`이 부재하거나 Python 2일 수 있어 `python3` 사용.
        File.WriteAllText(Path.Combine(_root, "pyproject.toml"), "[project]");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith("python ", spec!.Command);
            Assert.DoesNotContain("python3", spec.Command);
        }
        else
        {
            Assert.StartsWith("python3 ", spec!.Command);
        }
    }

    [Fact]
    public void SetupPy_marker_infers_python_compileall()
    {
        File.WriteAllText(Path.Combine(_root, "setup.py"), "from setuptools import setup");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Contains("compileall", spec!.Command);
    }

    [Fact]
    public void RequirementsTxt_marker_infers_python_compileall()
    {
        File.WriteAllText(Path.Combine(_root, "requirements.txt"), "");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Contains("compileall", spec!.Command);
    }

    [Fact]
    public void Empty_directory_returns_null()
    {
        var spec = SmokeTestPlanner.Infer(_root);

        Assert.Null(spec);
    }

    [Fact]
    public void Nonexistent_directory_returns_null()
    {
        var spec = SmokeTestPlanner.Infer(Path.Combine(_root, "does-not-exist"));

        Assert.Null(spec);
    }

    // ─── package.json: scripts 인식 ──────────────────────────────────────────

    [Fact]
    public void PackageJson_with_build_script_uses_npm_run_build_by_default()
    {
        File.WriteAllText(Path.Combine(_root, "package.json"),
            "{\"scripts\": {\"build\": \"next build\"}}");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Equal("npm run build", spec!.Command);
    }

    [Fact]
    public void PackageJson_with_pnpm_lock_uses_pnpm_run_build()
    {
        File.WriteAllText(Path.Combine(_root, "package.json"),
            "{\"scripts\": {\"build\": \"next build\"}}");
        File.WriteAllText(Path.Combine(_root, "pnpm-lock.yaml"), "");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Equal("pnpm run build", spec!.Command);
    }

    [Fact]
    public void PackageJson_with_yarn_lock_uses_yarn_run_build()
    {
        File.WriteAllText(Path.Combine(_root, "package.json"),
            "{\"scripts\": {\"build\": \"vite build\"}}");
        File.WriteAllText(Path.Combine(_root, "yarn.lock"), "");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Equal("yarn run build", spec!.Command);
    }

    [Fact]
    public void PackageJson_with_bun_lock_uses_bun_run_build()
    {
        File.WriteAllText(Path.Combine(_root, "package.json"),
            "{\"scripts\": {\"build\": \"bun build src\"}}");
        File.WriteAllText(Path.Combine(_root, "bun.lockb"), "");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Equal("bun run build", spec!.Command);
    }

    [Fact]
    public void PackageJson_no_build_with_tsconfig_uses_tsc_noemit()
    {
        File.WriteAllText(Path.Combine(_root, "package.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "tsconfig.json"), "{}");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Contains("tsc --noEmit", spec!.Command);
    }

    [Fact]
    public void PackageJson_with_build_and_test_scripts_chains_them()
    {
        // Option A: per-task verification은 compile만 하고 실제 test 실행은 smoke가 담당.
        // 따라서 build와 test가 둘 다 있으면 chain하여 한 번에 검증한다.
        File.WriteAllText(Path.Combine(_root, "package.json"),
            "{\"scripts\": {\"build\": \"vite build\", \"test\": \"vitest run\"}}");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Equal("npm run build && npm test --silent", spec!.Command);
    }

    [Fact]
    public void PackageJson_with_build_and_test_scripts_pnpm_chains_with_recursive_build()
    {
        // pnpm + workspaces인 경우 build에는 -r 추가, test는 그대로.
        File.WriteAllText(Path.Combine(_root, "package.json"),
            "{\"workspaces\": [\"packages/*\"], \"scripts\": {\"build\": \"turbo run build\", \"test\": \"vitest run\"}}");
        File.WriteAllText(Path.Combine(_root, "pnpm-lock.yaml"), "");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Equal("pnpm -r run build && pnpm test --silent", spec!.Command);
    }

    [Fact]
    public void PackageJson_with_test_script_only_uses_pm_test()
    {
        File.WriteAllText(Path.Combine(_root, "package.json"),
            "{\"scripts\": {\"test\": \"jest\"}}");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Contains("npm test", spec!.Command);
    }

    [Fact]
    public void PackageJson_no_scripts_no_tsconfig_skips_node()
    {
        // 핵심 회귀 방지: Next.js 기본 템플릿처럼 test 스크립트가 없으면 npm test를 강제하지 않는다.
        File.WriteAllText(Path.Combine(_root, "package.json"), "{}");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.Null(spec);
    }

    [Fact]
    public void PackageJson_with_workspaces_array_and_pnpm_uses_recursive_build()
    {
        File.WriteAllText(Path.Combine(_root, "package.json"),
            "{\"workspaces\": [\"packages/*\"], \"scripts\": {\"build\": \"turbo run build\"}}");
        File.WriteAllText(Path.Combine(_root, "pnpm-lock.yaml"), "");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Equal("pnpm -r run build", spec!.Command);
    }

    [Fact]
    public void PackageJson_with_workspaces_and_npm_does_not_use_recursive()
    {
        // npm은 -r을 이해하지 못하므로 root build script만 실행.
        File.WriteAllText(Path.Combine(_root, "package.json"),
            "{\"workspaces\": [\"packages/*\"], \"scripts\": {\"build\": \"echo build\"}}");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Equal("npm run build", spec!.Command);
    }

    [Fact]
    public void PnpmWorkspaceYaml_signals_monorepo_even_without_workspaces_field()
    {
        File.WriteAllText(Path.Combine(_root, "package.json"),
            "{\"scripts\": {\"build\": \"echo build\"}}");
        File.WriteAllText(Path.Combine(_root, "pnpm-lock.yaml"), "");
        File.WriteAllText(Path.Combine(_root, "pnpm-workspace.yaml"), "packages:\n  - apps/*");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Equal("pnpm -r run build", spec!.Command);
    }

    // ─── 다중 마커 결합 ────────────────────────────────────────────────────────

    [Fact]
    public void Multi_marker_combines_all_with_and_separator()
    {
        // .NET 백엔드 + Next.js 프론트가 한 repo에 있으면 둘 다 검증되어야 한다.
        File.WriteAllText(Path.Combine(_root, "Foo.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "package.json"),
            "{\"scripts\": {\"build\": \"next build\"}}");

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Contains("dotnet build", spec!.Command);
        Assert.Contains("npm run build", spec.Command);
        Assert.Contains("&&", spec.Command);
    }

    [Fact]
    public void Multi_marker_max_timeout_is_used()
    {
        File.WriteAllText(Path.Combine(_root, "Foo.csproj"), "<Project />");      // 180s
        File.WriteAllText(Path.Combine(_root, "package.json"),
            "{\"scripts\": {\"build\": \"x\"}}");                                  // 300s

        var spec = SmokeTestPlanner.Infer(_root);

        Assert.NotNull(spec);
        Assert.Equal(300, spec!.TimeoutSec);
    }

    // ─── Plan: 우선순위 및 docs-only 스킵 ──────────────────────────────────────

    [Fact]
    public void Plan_no_smoke_test_returns_null()
    {
        File.WriteAllText(Path.Combine(_root, "Foo.csproj"), "<Project />");

        var spec = SmokeTestPlanner.Plan(
            _root, configured: null, cliCommand: null, envCommand: null,
            noSmokeTest: true);

        Assert.Null(spec);
    }

    [Fact]
    public void Plan_cli_command_overrides_workflow_and_inference()
    {
        File.WriteAllText(Path.Combine(_root, "Foo.csproj"), "<Project />");
        var configured = new VerificationSpec { Command = "echo workflow", TimeoutSec = 60 };

        var spec = SmokeTestPlanner.Plan(
            _root, configured: configured, cliCommand: "echo cli", envCommand: "echo env",
            noSmokeTest: false);

        Assert.NotNull(spec);
        Assert.Equal("echo cli", spec!.Command);
    }

    [Fact]
    public void Plan_env_command_overrides_workflow_and_inference()
    {
        File.WriteAllText(Path.Combine(_root, "Foo.csproj"), "<Project />");
        var configured = new VerificationSpec { Command = "echo workflow", TimeoutSec = 60 };

        var spec = SmokeTestPlanner.Plan(
            _root, configured: configured, cliCommand: null, envCommand: "echo env",
            noSmokeTest: false);

        Assert.NotNull(spec);
        Assert.Equal("echo env", spec!.Command);
    }

    [Fact]
    public void Plan_workflow_takes_precedence_over_inference()
    {
        File.WriteAllText(Path.Combine(_root, "Foo.csproj"), "<Project />");
        var configured = new VerificationSpec { Command = "echo workflow", TimeoutSec = 60 };

        var spec = SmokeTestPlanner.Plan(
            _root, configured: configured, cliCommand: null, envCommand: null,
            noSmokeTest: false);

        Assert.NotNull(spec);
        Assert.Equal("echo workflow", spec!.Command);
        Assert.Equal(60, spec.TimeoutSec);
    }

    [Fact]
    public void Plan_falls_through_to_inference_when_nothing_configured()
    {
        File.WriteAllText(Path.Combine(_root, "go.mod"), "module foo");

        var spec = SmokeTestPlanner.Plan(
            _root, configured: null, cliCommand: null, envCommand: null,
            noSmokeTest: false);

        Assert.NotNull(spec);
        Assert.Contains("go build", spec!.Command);
    }

    [Fact]
    public void Plan_docs_only_changes_skip_inference()
    {
        File.WriteAllText(Path.Combine(_root, "go.mod"), "module foo");
        var changed = new[] { "README.md", "docs/architecture.md", "CHANGELOG" };

        var spec = SmokeTestPlanner.Plan(
            _root, configured: null, cliCommand: null, envCommand: null,
            noSmokeTest: false, changedFiles: changed);

        Assert.Null(spec);
    }

    [Fact]
    public void Plan_docs_only_skip_does_not_apply_to_workflow_smokeTest()
    {
        // 사용자가 명시적으로 설정했으면 docs만 변경되어도 항상 실행 (intent 존중).
        var configured = new VerificationSpec { Command = "echo workflow", TimeoutSec = 60 };
        var changed = new[] { "README.md" };

        var spec = SmokeTestPlanner.Plan(
            _root, configured: configured, cliCommand: null, envCommand: null,
            noSmokeTest: false, changedFiles: changed);

        Assert.NotNull(spec);
        Assert.Equal("echo workflow", spec!.Command);
    }

    [Fact]
    public void Plan_mixed_changes_do_not_skip_inference()
    {
        File.WriteAllText(Path.Combine(_root, "go.mod"), "module foo");
        // 코드 파일이 하나라도 있으면 docs-only가 아니므로 실행.
        var changed = new[] { "README.md", "main.go" };

        var spec = SmokeTestPlanner.Plan(
            _root, configured: null, cliCommand: null, envCommand: null,
            noSmokeTest: false, changedFiles: changed);

        Assert.NotNull(spec);
        Assert.Contains("go build", spec!.Command);
    }

    [Fact]
    public void Plan_no_changed_files_argument_runs_inference_normally()
    {
        File.WriteAllText(Path.Combine(_root, "go.mod"), "module foo");

        var spec = SmokeTestPlanner.Plan(
            _root, configured: null, cliCommand: null, envCommand: null,
            noSmokeTest: false, changedFiles: null);

        Assert.NotNull(spec);
        Assert.Contains("go build", spec!.Command);
    }
}
