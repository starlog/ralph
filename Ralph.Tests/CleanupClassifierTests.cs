using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

public class CleanupClassifierTests
{
    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        var report = CleanupClassifier.Classify("");
        Assert.Empty(report.SourceFiles);
        Assert.Empty(report.Artifacts);
        Assert.Empty(report.Other);
    }

    [Fact]
    public void SourceFiles_AreDetected()
    {
        var input = string.Join("\n",
            " M src/errors.ts",
            "?? src/foo.py",
            " M lib/Bar.cs");
        var report = CleanupClassifier.Classify(input);
        Assert.Equal(3, report.SourceFiles.Count);
        Assert.Contains("src/errors.ts", report.SourceFiles);
        Assert.Contains("src/foo.py", report.SourceFiles);
        Assert.Contains("lib/Bar.cs", report.SourceFiles);
        Assert.Empty(report.Artifacts);
    }

    [Fact]
    public void NodeModulesAndDist_AreArtifacts_NotSource()
    {
        var input = string.Join("\n",
            "?? node_modules/foo/bar.ts",
            "?? dist/index.js",
            " M target/debug/build.rs");
        var report = CleanupClassifier.Classify(input);
        Assert.Empty(report.SourceFiles);
        Assert.Equal(3, report.Artifacts.Count);
    }

    [Fact]
    public void Lockfiles_AreArtifacts()
    {
        var input = string.Join("\n",
            " M package-lock.json",
            " M yarn.lock",
            " M Cargo.lock",
            " M poetry.lock");
        var report = CleanupClassifier.Classify(input);
        Assert.Equal(4, report.Artifacts.Count);
        Assert.Empty(report.SourceFiles);
    }

    [Fact]
    public void TsBuildInfoAndPyc_AreArtifacts()
    {
        var input = string.Join("\n",
            "?? src/types.tsbuildinfo",
            "?? src/__pycache__/foo.pyc");
        var report = CleanupClassifier.Classify(input);
        Assert.Equal(2, report.Artifacts.Count);
    }

    [Fact]
    public void RenameLine_UsesNewPath()
    {
        // porcelain rename: "R  old.ts -> new.ts" — new.ts가 분류 기준
        var input = "R  src/old.ts -> src/new.ts";
        var report = CleanupClassifier.Classify(input);
        Assert.Single(report.SourceFiles);
        Assert.Contains("src/new.ts", report.SourceFiles);
    }

    [Fact]
    public void UnknownExtensions_AreOther_NotSource()
    {
        var input = string.Join("\n",
            "?? README.md",
            " M config.json",
            "?? notes.txt");
        var report = CleanupClassifier.Classify(input);
        Assert.Empty(report.SourceFiles);
        // .md는 docs라서 source 아님 — Other에 포함
        Assert.Equal(3, report.Other.Count);
    }

    [Fact]
    public void MixedInput_IsClassifiedCorrectly()
    {
        // 실측에서 본 시나리오: undeclared source(errors.ts) + 정상 artifact(node_modules) + lockfile.
        var input = string.Join("\n",
            "?? src/errors.ts",
            "?? node_modules/.package-lock.json",
            " M package-lock.json",
            "?? coverage/lcov.info",
            " M src/discard.ts");
        var report = CleanupClassifier.Classify(input);
        Assert.Equal(2, report.SourceFiles.Count);
        Assert.Contains("src/errors.ts", report.SourceFiles);
        Assert.Contains("src/discard.ts", report.SourceFiles);
        Assert.Equal(3, report.Artifacts.Count);
    }
}
