using CoverageCompletion.Contracts;
using CoverageCompletion.Infrastructure.Git;
using FluentAssertions;

namespace CoverageCompletion.Infrastructure.Tests;

public class GitCommitterTests : IDisposable
{
    private readonly TempGitRepo _repo = new();
    private readonly WorktreeManager _worktreeManager = new();
    private readonly GitCommitter _sut = new();
    private WorktreeSession? _session;

    [Fact]
    public async Task CommitFileAsync_CommitsFileAndReturnsTheResultingCommitSha()
    {
        _session = await _worktreeManager.CreateAsync(_repo.Path, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_session.WorktreePath, "NewFile.txt"), "hello");

        var sha = await _sut.CommitFileAsync(
            _session.WorktreePath,
            "NewFile.txt",
            "add NewFile.txt",
            CancellationToken.None);

        sha.Should().MatchRegex("^[0-9a-f]{40}$");

        var log = GitCli.Run(_session.WorktreePath, "log", "-1", "--pretty=%H %s");
        log.Should().Be($"{sha} add NewFile.txt");
    }

    public void Dispose()
    {
        if (_session is not null && Directory.Exists(_session.WorktreePath))
        {
            try
            {
                _worktreeManager.RemoveAsync(_session, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
                // best effort cleanup
            }
        }

        _repo.Dispose();
    }
}
