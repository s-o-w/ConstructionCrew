using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Providers;

namespace ConstructionCrew.Tests.GitTests;

/// <summary>
/// A throwaway on-disk git repo. These are the tests that deliberately DO spawn
/// real processes -- WorktreeManager's whole job is shelling git, and a fake
/// runner would only prove that the argv strings were assembled, not that git
/// accepts them.
/// </summary>
internal sealed class GitScratchRepo : IDisposable
{
    public string Root { get; }
    public string RepoPath { get; }
    public string FeatureBranch => "feature/thing";

    private readonly CliProcessRunner _runner = new();

    private GitScratchRepo(string root, string repoPath)
    {
        Root = root;
        RepoPath = repoPath;
    }

    public static async Task<GitScratchRepo> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-git-" + Guid.NewGuid().ToString("n")[..10]);
        var repoPath = Path.Combine(root, "repo");
        Directory.CreateDirectory(repoPath);

        var repo = new GitScratchRepo(root, repoPath);

        await repo.GitOrThrow(repoPath, "init", "-b", "main");
        // Local config only -- the machine's global identity/signing settings must
        // not decide whether this test can commit.
        await repo.GitOrThrow(repoPath, "config", "user.email", "crew@constructioncrew.test");
        await repo.GitOrThrow(repoPath, "config", "user.name", "ConstructionCrew Tests");
        await repo.GitOrThrow(repoPath, "config", "commit.gpgsign", "false");

        Directory.CreateDirectory(Path.Combine(repoPath, "src"));
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "tracked content\n");
        File.WriteAllText(Path.Combine(repoPath, "src", "app.txt"), "tracked source\n");
        await repo.GitOrThrow(repoPath, "add", ".");
        await repo.GitOrThrow(repoPath, "commit", "-m", "initial");
        await repo.GitOrThrow(repoPath, "checkout", "-b", repo.FeatureBranch);

        return repo;
    }

    public string WorktreePath(string name) => Path.Combine(Root, "worktrees", name);

    public Task<CliRunResult> Git(string workingDirectory, params string[] args) =>
        _runner.RunAsync(new CliInvocation("git", args, workingDirectory), CancellationToken.None);

    public async Task<string> GitOrThrow(string workingDirectory, params string[] args)
    {
        var result = await Git(workingDirectory, args);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed in {workingDirectory}: {result.StandardError}{result.StandardOutput}");
        }

        return result.StandardOutput;
    }

    /// <summary>Stages and commits everything currently in <paramref name="workingDirectory"/>.</summary>
    public async Task CommitAllAsync(string workingDirectory, string message)
    {
        await GitOrThrow(workingDirectory, "add", "-A");
        await GitOrThrow(workingDirectory, "commit", "-m", message);
    }

    /// <summary>Every file under the repo EXCEPT .git, with its content -- the "tracked content untouched" snapshot.</summary>
    public Dictionary<string, string> SnapshotWorkingTree()
    {
        var gitDir = Path.Combine(RepoPath, ".git") + Path.DirectorySeparatorChar;

        return Directory.EnumerateFiles(RepoPath, "*", SearchOption.AllDirectories)
            .Where(p => !p.StartsWith(gitDir, StringComparison.Ordinal))
            .ToDictionary(
                p => Path.GetRelativePath(RepoPath, p),
                File.ReadAllText,
                StringComparer.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp dir is never worth failing a green test over.
        }
    }
}
