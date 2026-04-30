using Ralph.Commands;
using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

/// <summary>
/// safe-permissions 모드 동작 검증.
/// CLI --safe-permissions 플래그, RALPH_REQUIRE_PERMISSIONS env var,
/// 그리고 두 값의 우선순위(CLI || env) 논리를 테스트한다.
/// </summary>
public class SafePermissionsTests
{
    // ─── 1. 기본 호출: --dangerously-skip-permissions 포함 모드 ────────────────

    [Fact]
    public void ClaudeService_Default_SafePermissionsFalse()
    {
        // SafePermissions = false(기본값) → RunStreamAsync가 --dangerously-skip-permissions를 args에 추가한다
        var svc = new ClaudeService();
        Assert.False(svc.SafePermissions);
    }

    // ─── 2. --safe-permissions CLI 옵션: --dangerously-skip-permissions 제거 ──

    [Fact]
    public void ArgParser_SafePermissionsFlag_ActivatesSafeMode()
    {
        var ctx = ArgParser.Parse(["--run", "--safe-permissions"]);

        Assert.NotNull(ctx);
        Assert.True(ctx.CliSafePermissions);
        Assert.True(ctx.SafePermissions); // SafePermissions = true → dangerously 플래그 미부착
    }

    // ─── 3. env RALPH_REQUIRE_PERMISSIONS=true, CLI 없음 → safe 모드 활성 ────

    [Fact]
    public void ArgParser_EnvRequirePermissionsTrue_ActivatesSafeMode()
    {
        WithEnv("RALPH_REQUIRE_PERMISSIONS", "true", () =>
        {
            var ctx = ArgParser.Parse(["--run"]);

            Assert.NotNull(ctx);
            Assert.True(ctx.EnvRequirePermissions);
            Assert.False(ctx.CliSafePermissions);   // CLI 플래그 없음
            Assert.True(ctx.SafePermissions);       // env만으로 safe 모드 활성
        });
    }

    // ─── 4. 우선순위: env true + CLI 없음 → safe; env unset + CLI 없음 → dangerously ─

    [Theory]
    [InlineData("true", false, true)]   // env=true, CLI 없음 → safe
    [InlineData(null,   false, false)]  // env unset, CLI 없음 → dangerously
    public void SafePermissions_Priority_EnvVsCli(string? envValue, bool cliFlag, bool expectedSafe)
    {
        WithEnv("RALPH_REQUIRE_PERMISSIONS", envValue, () =>
        {
            var args = cliFlag
                ? new[] { "--run", "--safe-permissions" }
                : new[] { "--run" };
            var ctx = ArgParser.Parse(args);

            Assert.NotNull(ctx);
            Assert.Equal(expectedSafe, ctx.SafePermissions);
        });
    }

    // ─── helper ──────────────────────────────────────────────────────────────

    private static void WithEnv(string key, string? value, Action action)
    {
        var prev = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, value);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, prev);
        }
    }
}
