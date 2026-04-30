using System.Diagnostics;
using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

/// <summary>
/// GitService.BuildGitProcessStartInfo가 항상 LC_ALL=C / LANG=C 환경변수를 설정하는지
/// 검증. 실제 git 바이너리를 호출하지 않고 ProcessStartInfo 객체를 직접 검사한다.
/// InternalsVisibleTo("Ralph.Tests")로 internal 멤버에 접근 가능.
/// </summary>
public class GitServiceLocaleTests
{
    [Fact]
    public void BuildGitProcessStartInfo_sets_LC_ALL_to_C()
    {
        var psi = GitService.BuildGitProcessStartInfo(["status"]);

        Assert.True(psi.Environment.ContainsKey("LC_ALL"),
            "ProcessStartInfo.Environment에 LC_ALL이 없습니다.");
        Assert.Equal("C", psi.Environment["LC_ALL"]);
    }

    [Fact]
    public void BuildGitProcessStartInfo_sets_LANG_to_C()
    {
        var psi = GitService.BuildGitProcessStartInfo(["status"]);

        Assert.True(psi.Environment.ContainsKey("LANG"),
            "ProcessStartInfo.Environment에 LANG이 없습니다.");
        Assert.Equal("C", psi.Environment["LANG"]);
    }

    [Fact]
    public void BuildGitProcessStartInfo_locale_vars_present_for_any_subcommand()
    {
        string[][] commands = [["log"], ["diff", "--name-only"], ["rev-parse", "--abbrev-ref", "HEAD"]];
        foreach (var args in commands)
        {
            var psi = GitService.BuildGitProcessStartInfo(args);
            Assert.Equal("C", psi.Environment["LC_ALL"]);
            Assert.Equal("C", psi.Environment["LANG"]);
        }
    }

    [Fact]
    public void BuildGitProcessStartInfo_sets_working_directory_when_provided()
    {
        var psi = GitService.BuildGitProcessStartInfo(["log"], "/tmp");

        Assert.Equal("/tmp", psi.WorkingDirectory);
    }

    [Fact]
    public void BuildGitProcessStartInfo_working_directory_empty_when_not_provided()
    {
        var psi = GitService.BuildGitProcessStartInfo(["status"]);

        Assert.Equal("", psi.WorkingDirectory);
    }

    [Fact]
    public void BuildGitProcessStartInfo_adds_all_arguments_in_order()
    {
        var args = new[] { "diff", "--name-only", "main...HEAD" };
        var psi = GitService.BuildGitProcessStartInfo(args);

        Assert.Equal(args, psi.ArgumentList.ToArray());
    }

    [Fact]
    public void BuildGitProcessStartInfo_uses_git_binary()
    {
        var psi = GitService.BuildGitProcessStartInfo(["status"]);

        Assert.Equal("git", psi.FileName);
    }

    [Fact]
    public void BuildGitProcessStartInfo_redirects_stdout_and_stderr()
    {
        var psi = GitService.BuildGitProcessStartInfo(["status"]);

        Assert.False(psi.UseShellExecute);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
    }
}
