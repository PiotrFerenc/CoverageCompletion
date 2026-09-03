using CoverageCompletion.Contracts;
using CoverageCompletion.Infrastructure.Git;
using Shouldly;

namespace CoverageCompletion.Infrastructure.Tests;

public class GitCommitterTests : IDisposable
{
    private readonly TempGitRepo _repo = new();
    private readonly WorktreeManager _worktreeManager = new();
    private readonly GitCommitter _sut = new();
    private WorktreeSession? _session;

    [Fact]
    public async Task CommitFilesAsync_CommitsFileAndReturnsTheResultingCommitSha()
    {
        _session = await _worktreeManager.CreateAsync(_repo.Path, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_session.WorktreePath, "NewFile.txt"), "hello");

        var sha = await _sut.CommitFilesAsync(
            _session.WorktreePath,
            ["NewFile.txt"],
            "add NewFile.txt",
            CancellationToken.None);

        sha.ShouldMatch("^[0-9a-f]{40}$");

        var log = GitCli.Run(_session.WorktreePath, "log", "-1", "--pretty=%H %s");
        log.ShouldBe($"{sha} add NewFile.txt");
    }

    [Fact]
    public async Task CommitFilesAsync_MultipleFiles_CommitsBothInOneCommit()
    {
        _session = await _worktreeManager.CreateAsync(_repo.Path, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_session.WorktreePath, "First.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(_session.WorktreePath, "Second.txt"), "two");

        var sha = await _sut.CommitFilesAsync(
            _session.WorktreePath,
            ["First.txt", "Second.txt"],
            "add both files",
            CancellationToken.None);

        var changedFiles = GitCli.Run(_session.WorktreePath, "show", "--name-only", "--pretty=format:", sha);
        changedFiles.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ShouldBe(["First.txt", "Second.txt"], ignoreOrder: true);
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
