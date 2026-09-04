using System.Text;
using CoverageCompletion.Contracts;

namespace CoverageCompletion.Infrastructure.Git;

/// <summary>
/// Merges one or more finished session branches, in order, into a fresh branch cut from the
/// branch the sessions started on, via a temporary worktree - never touches the caller's
/// actual working tree.
/// </summary>
public sealed class BranchMerger : IBranchMerger
{
    public async Task<MergeOutcome> MergeSessionsIntoNewBranchAsync(
        string repoPath, string baseBranch, IReadOnlyList<string> sessionBranches, CancellationToken ct)
    {
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var targetBranch = $"coverage/merged-{DateTime.Now:yyyyMMdd-HHmmss}-{uniqueSuffix}";
        var worktreeRoot = Path.Combine(Path.GetTempPath(), "coverage-worktrees");
        var worktreePath = Path.Combine(worktreeRoot, targetBranch.Replace('/', '-'));

        Directory.CreateDirectory(worktreeRoot);

        await GitProcess.RunOrThrowAsync(repoPath, ct, "worktree", "add", "-b", targetBranch, worktreePath, baseBranch);

        var combinedOutput = new StringBuilder();
        foreach (var sessionBranch in sessionBranches)
        {
            var mergeResult = await GitProcess.RunAsync(worktreePath, ct, "merge", "--no-edit", sessionBranch);
            combinedOutput.AppendLine(mergeResult.CombinedOutput);

            if (mergeResult.ExitCode != 0)
            {
                // Conflict: leave the worktree in place, unresolved - including whatever earlier
                // branches already merged cleanly - for the user to fix by hand.
                return new MergeOutcome(targetBranch, worktreePath, HasConflicts: true, combinedOutput.ToString());
            }
        }

        await GitProcess.RunOrThrowAsync(repoPath, ct, "worktree", "remove", worktreePath, "--force");
        return new MergeOutcome(targetBranch, worktreePath, HasConflicts: false, combinedOutput.ToString());
    }
}
