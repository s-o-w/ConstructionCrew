using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Git;
using ConstructionCrew.Tests.Fakes;

namespace ConstructionCrew.Tests.GitTests;

/// <summary>
/// The passive column's read side (Phase 8b step 4): git status / git log for a
/// driven Foreman's worktree, shelled through the same ICliProcessRunner seam
/// WorktreeManager already uses. Read-only, and never fatal -- a worktree that
/// has been cleaned up under it must degrade to a line in the panel, not take the
/// Boss loop down.
/// </summary>
public class GitWorkspaceInspectorTests
{
    /// <summary>
    /// Just a directory that exists. The inspector's git calls all go through the
    /// fake runner -- what the real WorktreeManager tests need a live repo for
    /// (does git accept this argv) is not what is under test here.
    /// </summary>
    private sealed class ScratchDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cc-inspect-" + Guid.NewGuid().ToString("n")[..10]);

        public ScratchDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static FakeCliProcessRunner Runner(string statusOut, string logOut, bool statusOk = true) =>
        new()
        {
            Handler = invocation => invocation.Arguments[0] switch
            {
                "status" => new CliRunResult(statusOk, statusOut, statusOk ? "" : statusOut, statusOk ? 0 : 128),
                "log" => new CliRunResult(true, logOut, "", 0),
                _ => new CliRunResult(false, "", "unexpected git command", 1),
            },
        };

    [Fact]
    public async Task Inspect_ReadsBranchChangeCountAndRecentCommits()
    {
        using var repo = new ScratchDirectory();
        var runner = Runner(
            "## feature/phase-8...origin/feature/phase-8 [ahead 2]\n M src/Program.cs\n?? notes.md\n",
            "abc1234 wire the boss loop\ndef5678 add the inspector\n");

        var snapshot = await new GitWorkspaceInspector(runner).InspectAsync(repo.Path, CancellationToken.None);

        Assert.Null(snapshot.Error);
        Assert.Equal("feature/phase-8", snapshot.Branch);
        Assert.Equal(2, snapshot.ChangedFiles);
        Assert.Equal(["abc1234 wire the boss loop", "def5678 add the inspector"], snapshot.RecentCommits);
    }

    /// <summary>Both reads run in the worktree, never the process-wide current directory.</summary>
    [Fact]
    public async Task Inspect_RunsGitInsideTheWorktree()
    {
        using var repo = new ScratchDirectory();
        var runner = Runner("## main\n", "");

        await new GitWorkspaceInspector(runner).InspectAsync(repo.Path, CancellationToken.None);

        Assert.Equal(2, runner.Invocations.Count);
        Assert.All(runner.Invocations, i =>
        {
            Assert.Equal("git", i.ExecutablePath);
            Assert.Equal(repo.Path, i.WorkingDirectory);
        });
        Assert.Equal("status", runner.Invocations[0].Arguments[0]);
        Assert.Equal("log", runner.Invocations[1].Arguments[0]);
    }

    [Fact]
    public async Task Inspect_CleanTreeCountsNoChanges()
    {
        using var repo = new ScratchDirectory();
        var runner = Runner("## main...origin/main\n", "abc1234 initial\n");

        var snapshot = await new GitWorkspaceInspector(runner).InspectAsync(repo.Path, CancellationToken.None);

        Assert.Equal("main", snapshot.Branch);
        Assert.Equal(0, snapshot.ChangedFiles);
    }

    /// <summary>A missing worktree is reported, not thrown.</summary>
    [Fact]
    public async Task Inspect_MissingWorktree_ReportsAnError()
    {
        var runner = Runner("", "");

        var snapshot = await new GitWorkspaceInspector(runner)
            .InspectAsync(Path.Combine(Path.GetTempPath(), "cc-not-a-worktree-" + Guid.NewGuid().ToString("n")), CancellationToken.None);

        Assert.NotNull(snapshot.Error);
        Assert.Empty(runner.Invocations);
    }

    /// <summary>So is a git that refuses.</summary>
    [Fact]
    public async Task Inspect_FailedStatus_ReportsAnError()
    {
        using var repo = new ScratchDirectory();
        var runner = Runner("not a git repository", "", statusOk: false);

        var snapshot = await new GitWorkspaceInspector(runner).InspectAsync(repo.Path, CancellationToken.None);

        Assert.Contains("not a git repository", snapshot.Error);
        Assert.Null(snapshot.Branch);
    }

    [Theory]
    [InlineData("## main...origin/main [ahead 2, behind 1]", "main")]
    [InlineData("## feature/x", "feature/x")]
    [InlineData("## HEAD (no branch)", "HEAD (no branch)")]
    public void ParseStatus_TrimsUpstreamAndTrackingInfoOffTheBranchName(string header, string expected)
    {
        var (branch, changed) = GitWorkspaceInspector.ParseStatus(header + "\n");

        Assert.Equal(expected, branch);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void ParseStatus_WithNoHeader_HasNoBranchButStillCounts()
    {
        var (branch, changed) = GitWorkspaceInspector.ParseStatus(" M a.cs\n M b.cs\n");

        Assert.Null(branch);
        Assert.Equal(2, changed);
    }
}
