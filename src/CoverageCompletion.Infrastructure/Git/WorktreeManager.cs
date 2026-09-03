using CoverageCompletion.Contracts;

namespace CoverageCompletion.Infrastructure.Git;

/// <summary>
/// Creates/removes an isolated git worktree + branch so coverage-completion runs don't touch
/// the caller's working tree.
/// </summary>
public sealed class WorktreeManager : IWorktreeManager
{
    public async Task<WorktreeSession> CreateAsync(string repoPath, CancellationToken ct)
    {
        var currentBranch = await GitProcess.RunOrThrowAsync(repoPath, ct, "rev-parse", "--abbrev-ref", "HEAD");

        // A short random suffix guarantees uniqueness even when two sessions start in the same
        // second (e.g. concurrent runs), which a bare timestamp does not.
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var branchName = $"coverage/session-{DateTime.Now:yyyyMMdd-HHmmss}-{uniqueSuffix}";
        var worktreeRoot = Path.Combine(Path.GetTempPath(), "coverage-worktrees");
        var worktreePath = Path.Combine(worktreeRoot, branchName.Replace('/', '-'));

        Directory.CreateDirectory(worktreeRoot);

        await GitProcess.RunOrThrowAsync(repoPath, ct, "worktree", "add", "-b", branchName, worktreePath, currentBranch);

        return new WorktreeSession(repoPath, worktreePath, branchName, currentBranch);
    }

    public async Task RemoveAsync(WorktreeSession session, CancellationToken ct)
    {
        await GitProcess.RunOrThrowAsync(session.RepoPath, ct, "worktree", "remove", session.WorktreePath, "--force");
    }
}
