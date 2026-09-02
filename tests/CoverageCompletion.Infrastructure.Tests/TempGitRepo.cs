namespace CoverageCompletion.Infrastructure.Tests;

/// <summary>
/// A real temporary git repository (git init + one commit) under the OS temp dir, used to
/// integration-test WorktreeManager/GitCommitter against actual git plumbing instead of mocks.
/// </summary>
public sealed class TempGitRepo : IDisposable
{
    public string Path { get; }

    public TempGitRepo()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cc-infra-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);

        GitCli.Run(Path, "init", "-b", "main");
        GitCli.Run(Path, "config", "user.email", "test@example.com");
        GitCli.Run(Path, "config", "user.name", "Coverage Completion Tests");

        File.WriteAllText(System.IO.Path.Combine(Path, "README.md"), "seed" + Environment.NewLine);
        GitCli.Run(Path, "add", "README.md");
        GitCli.Run(Path, "commit", "-m", "initial commit");
    }

    public void Dispose()
    {
        try
        {
            // Drop any worktree admin files still registered against this repo before deleting it.
            GitCli.Run(Path, "worktree", "prune");
        }
        catch
        {
            // best effort cleanup
        }

        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }
}
