using CoverageCompletion.Contracts;

namespace CoverageCompletion.Infrastructure.Git;

/// <summary>
/// Stages and commits a single file inside a worktree, returning the resulting commit SHA.
/// </summary>
public sealed class GitCommitter : IGitCommitter
{
    public async Task<string> CommitFileAsync(string worktreePath, string relativeFilePath, string message, CancellationToken ct)
    {
        await GitProcess.RunOrThrowAsync(worktreePath, ct, "add", relativeFilePath);
        await GitProcess.RunOrThrowAsync(worktreePath, ct, "commit", "-m", message);
        return await GitProcess.RunOrThrowAsync(worktreePath, ct, "rev-parse", "HEAD");
    }
}
