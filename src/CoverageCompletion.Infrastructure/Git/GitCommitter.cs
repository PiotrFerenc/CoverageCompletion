using CoverageCompletion.Contracts;

namespace CoverageCompletion.Infrastructure.Git;

/// <summary>
/// Stages and commits one or more files inside a worktree in a single commit, returning the
/// resulting commit SHA.
/// </summary>
public sealed class GitCommitter : IGitCommitter
{
    public async Task<string> CommitFilesAsync(string worktreePath, IReadOnlyList<string> relativeFilePaths, string message, CancellationToken ct)
    {
        await GitProcess.RunOrThrowAsync(worktreePath, ct, ["add", .. relativeFilePaths]);
        await GitProcess.RunOrThrowAsync(worktreePath, ct, "commit", "-m", message);
        return await GitProcess.RunOrThrowAsync(worktreePath, ct, "rev-parse", "HEAD");
    }
}
