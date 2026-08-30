using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Git;

/// <summary>
/// What the passive column shows for a driven Foreman: which branch its worktree
/// is on, how much is uncommitted, and the last few commits. Nothing here is
/// interactive -- it is a read-only view of a working tree the Boss is not in.
/// </summary>
public sealed record GitWorkspaceSnapshot(
    string WorktreePath,
    string? Branch,
    int ChangedFiles,
    IReadOnlyList<string> RecentCommits,
    string? Error = null);

/// <summary>
/// Read-only <c>git status</c> / <c>git log</c> for one worktree, shelled through
/// the same <see cref="ICliProcessRunner"/> seam <see cref="WorktreeManager"/>
/// already uses. No new process-spawning code path enters the app here, and this
/// type never writes to a repo.
/// </summary>
public sealed class GitWorkspaceInspector
{
    private const string GitExecutable = "git";

    /// <summary>How many log entries the passive column has room for.</summary>
    internal const int RecentCommitCount = 5;

    private readonly ICliProcessRunner _runner;

    public GitWorkspaceInspector(ICliProcessRunner runner)
    {
        _runner = runner;
    }

    /// <summary>
    /// Never throws for a git-level failure: a Worker whose worktree was already
    /// removed must degrade to an <see cref="GitWorkspaceSnapshot.Error"/> line in
    /// the passive column, not take the Boss loop down with it.
    /// </summary>
    public async Task<GitWorkspaceSnapshot> InspectAsync(string worktreePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return new GitWorkspaceSnapshot(worktreePath, null, 0, [], "worktree is gone");
        }

        CliRunResult status;
        CliRunResult log;
        try
        {
            // --porcelain=v1 is the stable, parseable shape; --branch adds the
            // leading "## <branch>..." line the branch name comes from.
            status = await RunAsync(worktreePath, ["status", "--porcelain=v1", "--branch"], cancellationToken);
            log = await RunAsync(worktreePath, ["log", "--oneline", "-n", RecentCommitCount.ToString()], cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GitWorkspaceSnapshot(worktreePath, null, 0, [], ex.Message);
        }

        if (!status.Succeeded)
        {
            return new GitWorkspaceSnapshot(worktreePath, null, 0, [], Describe(status));
        }

        var (branch, changed) = ParseStatus(status.StandardOutput);
        var commits = log.Succeeded ? SplitLines(log.StandardOutput) : [];

        return new GitWorkspaceSnapshot(worktreePath, branch, changed, commits);
    }

    /// <summary>
    /// Splits <c>git status --porcelain=v1 --branch</c> into (branch, changed file
    /// count). The header line is <c>## &lt;branch&gt;...&lt;upstream&gt; [ahead N]</c>;
    /// a detached HEAD reports <c>## HEAD (no branch)</c>, which is passed through
    /// as-is rather than guessed at. Every other line is one changed path.
    /// </summary>
    internal static (string? Branch, int ChangedFiles) ParseStatus(string standardOutput)
    {
        string? branch = null;
        var changed = 0;

        foreach (var line in SplitLines(standardOutput))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var header = line[3..].Trim();
                var upstreamMarker = header.IndexOf("...", StringComparison.Ordinal);
                if (upstreamMarker >= 0)
                {
                    header = header[..upstreamMarker];
                }

                var trackingMarker = header.IndexOf(" [", StringComparison.Ordinal);
                if (trackingMarker >= 0)
                {
                    header = header[..trackingMarker];
                }

                branch = header.Trim();
                continue;
            }

            changed++;
        }

        return (string.IsNullOrWhiteSpace(branch) ? null : branch, changed);
    }

    private static IReadOnlyList<string> SplitLines(string text) =>
        text.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private Task<CliRunResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct) =>
        _runner.RunAsync(new CliInvocation(GitExecutable, arguments, workingDirectory), ct);

    private static string Describe(CliRunResult result)
    {
        var message = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return string.IsNullOrWhiteSpace(message) ? $"git exited {result.ExitCode}" : message.Trim();
    }
}
