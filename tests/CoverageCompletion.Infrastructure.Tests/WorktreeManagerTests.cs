using CoverageCompletion.Contracts;
using CoverageCompletion.Infrastructure.Git;
using FluentAssertions;

namespace CoverageCompletion.Infrastructure.Tests;

public class WorktreeManagerTests : IDisposable
{
    private readonly TempGitRepo _repo = new();
    private readonly WorktreeManager _sut = new();
    private WorktreeSession? _session;

    [Fact]
    public async Task CreateAsync_CreatesWorktreeCheckedOutOnNewCoverageBranch()
    {
        _session = await _sut.CreateAsync(_repo.Path, CancellationToken.None);

        _session.RepoPath.Should().Be(_repo.Path);
        _session.BranchName.Should().StartWith("coverage/session-");
        Directory.Exists(_session.WorktreePath).Should().BeTrue();
        File.Exists(Path.Combine(_session.WorktreePath, "README.md")).Should().BeTrue();

        var checkedOutBranch = GitCli.Run(_session.WorktreePath, "rev-parse", "--abbrev-ref", "HEAD");
        checkedOutBranch.Should().Be(_session.BranchName);
    }

    [Fact]
    public async Task RemoveAsync_DeletesTheWorktreeDirectory()
    {
        _session = await _sut.CreateAsync(_repo.Path, CancellationToken.None);

        await _sut.RemoveAsync(_session, CancellationToken.None);

        Directory.Exists(_session.WorktreePath).Should().BeFalse();
        _session = null; // already removed, nothing left for Dispose to clean up
    }

    public void Dispose()
    {
        if (_session is not null && Directory.Exists(_session.WorktreePath))
        {
            try
            {
                _sut.RemoveAsync(_session, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
                // best effort cleanup
            }
        }

        _repo.Dispose();
    }
}
