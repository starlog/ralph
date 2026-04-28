using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

public class SmokeTestInferenceTests : IDisposable
{
    private readonly string _root;

    public SmokeTestInferenceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ralph-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Csproj_marker_infers_dotnet_build()
    {
        File.WriteAllText(Path.Combine(_root, "Foo.csproj"), "<Project />");

        var spec = ParallelExecutor.InferSmokeTestCommand(_root);

        Assert.NotNull(spec);
        Assert.Contains("dotnet build", spec!.Command);
        Assert.Equal(180, spec.TimeoutSec);
    }

    [Fact]
    public void Sln_marker_infers_dotnet_build()
    {
        File.WriteAllText(Path.Combine(_root, "Foo.sln"), "");

        var spec = ParallelExecutor.InferSmokeTestCommand(_root);

        Assert.NotNull(spec);
        Assert.Contains("dotnet build", spec!.Command);
    }

    [Fact]
    public void PackageJson_marker_infers_npm_test()
    {
        File.WriteAllText(Path.Combine(_root, "package.json"), "{}");

        var spec = ParallelExecutor.InferSmokeTestCommand(_root);

        Assert.NotNull(spec);
        Assert.Contains("npm test", spec!.Command);
        Assert.Equal(180, spec.TimeoutSec);
    }

    [Fact]
    public void CargoToml_marker_infers_cargo_build()
    {
        File.WriteAllText(Path.Combine(_root, "Cargo.toml"), "[package]");

        var spec = ParallelExecutor.InferSmokeTestCommand(_root);

        Assert.NotNull(spec);
        Assert.Contains("cargo build", spec!.Command);
        Assert.Equal(300, spec.TimeoutSec);
    }

    [Fact]
    public void GoMod_marker_infers_go_build()
    {
        File.WriteAllText(Path.Combine(_root, "go.mod"), "module foo");

        var spec = ParallelExecutor.InferSmokeTestCommand(_root);

        Assert.NotNull(spec);
        Assert.Contains("go build", spec!.Command);
        Assert.Equal(180, spec.TimeoutSec);
    }

    [Fact]
    public void Empty_directory_returns_null()
    {
        var spec = ParallelExecutor.InferSmokeTestCommand(_root);

        Assert.Null(spec);
    }

    [Fact]
    public void Nonexistent_directory_returns_null()
    {
        var spec = ParallelExecutor.InferSmokeTestCommand(
            Path.Combine(_root, "does-not-exist"));

        Assert.Null(spec);
    }

    [Fact]
    public void Dotnet_takes_priority_over_other_markers()
    {
        // dotnet/.csproj가 우선순위 1번이어야 함을 확정
        File.WriteAllText(Path.Combine(_root, "Foo.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "package.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "go.mod"), "module foo");

        var spec = ParallelExecutor.InferSmokeTestCommand(_root);

        Assert.NotNull(spec);
        Assert.Contains("dotnet build", spec!.Command);
    }
}
