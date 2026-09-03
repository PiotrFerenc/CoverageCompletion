using CoverageCompletion.Contracts;

namespace CoverageCompletion.Infrastructure.Git;

/// <summary>
/// Merges a finished session branch into a fresh branch cut from the branch the session
/// started on, via a temporary worktree - never touches the caller's actual working tree.
/// </summary>
public sealed class BranchMerger : IBranchMerger
{
    public async Task<MergeOutcome> MergeSessionIntoNewBranchAsync(
        string repoPath, string baseBranch, string sessionBranch, CancellationToken ct)
    {
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var targetBranch = $"coverage/merged-{DateTime.Now:yyyyMMdd-HHmmss}-{uniqueSuffix}";
        var worktreeRoot = Path.Combine(Path.GetTempPath(), "coverage-worktrees");
        var worktreePath = Path.Combine(worktreeRoot, targetBranch.Replace('/', '-'));

        Directory.CreateDirectory(worktreeRoot);

        await GitProcess.RunOrThrowAsync(repoPath, ct, "worktree", "add", "-b", targetBranch, worktreePath, baseBranch);

        var mergeResult = await GitProcess.RunAsync(worktreePath, ct, "merge", "--no-edit", sessionBranch);
        if (mergeResult.ExitCode == 0)
        {
            await GitProcess.RunOrThrowAsync(repoPath, ct, "worktree", "remove", worktreePath, "--force");
            return new MergeOutcome(targetBranch, worktreePath, HasConflicts: false, mergeResult.CombinedOutput);
        }

        // Conflict: leave the worktree in place, unresolved, for the user to fix by hand.
        return new MergeOutcome(targetBranch, worktreePath, HasConflicts: true, mergeResult.CombinedOutput);
    }
}
