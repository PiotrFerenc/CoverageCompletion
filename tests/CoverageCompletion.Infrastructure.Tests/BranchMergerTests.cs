using CoverageCompletion.Infrastructure.Git;
using FluentAssertions;

namespace CoverageCompletion.Infrastructure.Tests;

public class BranchMergerTests : IDisposable
{
    private readonly TempGitRepo _repo = new();
    private readonly BranchMerger _sut = new();
    private string? _leftoverWorktreePath;

    [Fact]
    public async Task MergeSessionIntoNewBranchAsync_NoConflicts_CreatesBranchFromBase_ContainingTheSessionCommit_AndCleansUp()
    {
        GitCli.Run(_repo.Path, "checkout", "-b", "coverage/session-clean");
        File.WriteAllText(Path.Combine(_repo.Path, "NewFile.txt"), "hello");
        GitCli.Run(_repo.Path, "add", "NewFile.txt");
        GitCli.Run(_repo.Path, "commit", "-m", "test: cover NewFile");
        GitCli.Run(_repo.Path, "checkout", "main");

        var outcome = await _sut.MergeSessionIntoNewBranchAsync(_repo.Path, "main", "coverage/session-clean", CancellationToken.None);

        outcome.HasConflicts.Should().BeFalse();
        outcome.TargetBranch.Should().StartWith("coverage/merged-");
        Directory.Exists(outcome.TargetWorktreePath).Should().BeFalse("a clean merge cleans up its temporary worktree");

        var log = GitCli.Run(_repo.Path, "log", "--oneline", outcome.TargetBranch);
        log.Should().Contain("test: cover NewFile");
    }

    [Fact]
    public async Task MergeSessionIntoNewBranchAsync_Conflicts_LeavesWorktreeInPlaceForManualResolution()
    {
        GitCli.Run(_repo.Path, "checkout", "-b", "coverage/session-conflict");
        File.WriteAllText(Path.Combine(_repo.Path, "README.md"), "session change" + Environment.NewLine);
        GitCli.Run(_repo.Path, "add", "README.md");
        GitCli.Run(_repo.Path, "commit", "-m", "test: cover Conflicting");

        GitCli.Run(_repo.Path, "checkout", "main");
        File.WriteAllText(Path.Combine(_repo.Path, "README.md"), "base change" + Environment.NewLine);
        GitCli.Run(_repo.Path, "add", "README.md");
        GitCli.Run(_repo.Path, "commit", "-m", "unrelated base change");

        var outcome = await _sut.MergeSessionIntoNewBranchAsync(_repo.Path, "main", "coverage/session-conflict", CancellationToken.None);
        _leftoverWorktreePath = outcome.TargetWorktreePath;

        outcome.HasConflicts.Should().BeTrue();
        Directory.Exists(outcome.TargetWorktreePath).Should().BeTrue("the user needs a real checkout to resolve the conflict in");
        File.ReadAllText(Path.Combine(outcome.TargetWorktreePath, "README.md")).Should().Contain("<<<<<<<");
    }

    public void Dispose()
    {
        if (_leftoverWorktreePath is not null && Directory.Exists(_leftoverWorktreePath))
        {
            try
            {
                GitCli.Run(_repo.Path, "worktree", "remove", _leftoverWorktreePath, "--force");
            }
            catch
            {
                // best effort cleanup
            }
        }

        _repo.Dispose();
    }
}
