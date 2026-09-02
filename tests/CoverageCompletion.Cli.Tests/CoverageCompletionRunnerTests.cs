using CoverageCompletion.Contracts;
using FluentAssertions;

namespace CoverageCompletion.Cli.Tests;

public sealed class CoverageCompletionRunnerTests : IDisposable
{
    private readonly string _repoPath = Directory.CreateTempSubdirectory("cc-repo-").FullName;
    private readonly string _worktreePath = Directory.CreateTempSubdirectory("cc-worktree-").FullName;
    private readonly WorktreeSession _session;
    private readonly string _solutionPath;

    public CoverageCompletionRunnerTests()
    {
        _solutionPath = Path.Combine(_repoPath, "Solution.sln");
        _session = new WorktreeSession(_repoPath, _worktreePath, "coverage/branch-1");
    }

    public void Dispose()
    {
        Directory.Delete(_repoPath, recursive: true);
        Directory.Delete(_worktreePath, recursive: true);
    }

    private static CoverageGap Gap(string typeName) =>
        new("Foo.csproj", "Foo.cs", "MyNamespace", typeName, "DoWork", [10, 11]);

    private string FilePathFor(CoverageGap gap) =>
        Path.Combine(_worktreePath, "Tests", $"{gap.TypeName}Tests.cs");

    private static Func<BuildTestResult> Ok() => () => new BuildTestResult(true, "ok");

    private static Func<BuildTestResult> Fail(string output = "failed") => () => new BuildTestResult(false, output);

    [Fact]
    public async Task HappyPath_SingleGap_BuildAndTestSucceedFirstTry_CommitsAndReportsCompleted()
    {
        var gap = Gap("Widget");
        var worktreeManager = new FakeWorktreeManager(_session);
        var committer = new FakeGitCommitter();
        var reporter = new FakeSummaryReporter();
        var testGenerator = new FakeTestGenerator(FilePathFor);
        var buildRunner = new FakeBuildTestRunner([Ok()], [Ok()]);

        var runner = new CoverageCompletionRunner(
            worktreeManager, new FakeCoverageAnalyzer([gap]), buildRunner, committer, reporter, testGenerator);

        var exitCode = await runner.RunAsync(_repoPath, _solutionPath, CancellationToken.None);

        exitCode.Should().Be(0);
        reporter.Completed.Should().ContainSingle().Which.Gap.Should().Be(gap);
        reporter.Skipped.Should().BeEmpty();
        committer.Commits.Should().ContainSingle();
        testGenerator.RegenerateCallCount.Should().Be(0);
        worktreeManager.RemoveCallCount.Should().Be(1);
        reporter.WriteCallCount.Should().Be(1);
        File.Exists(FilePathFor(gap)).Should().BeTrue();
    }

    [Fact]
    public async Task RetryThenSucceed_BuildFailsOnce_RegeneratesAndCommitsOnSecondAttempt()
    {
        var gap = Gap("Widget");
        var worktreeManager = new FakeWorktreeManager(_session);
        var committer = new FakeGitCommitter();
        var reporter = new FakeSummaryReporter();
        var testGenerator = new FakeTestGenerator(FilePathFor);
        var buildRunner = new FakeBuildTestRunner([Fail("CS1002"), Ok()], [Ok()]);

        var runner = new CoverageCompletionRunner(
            worktreeManager, new FakeCoverageAnalyzer([gap]), buildRunner, committer, reporter, testGenerator,
            new RunnerOptions(MaxAttempts: 3));

        var exitCode = await runner.RunAsync(_repoPath, _solutionPath, CancellationToken.None);

        exitCode.Should().Be(0);
        testGenerator.RegenerateCallCount.Should().Be(1);
        buildRunner.BuildCallCount.Should().Be(2);
        committer.Commits.Should().ContainSingle();
        reporter.Completed.Should().ContainSingle();
        reporter.Skipped.Should().BeEmpty();
    }

    [Fact]
    public async Task RetryExhausted_BuildFailsEveryAttempt_RecordsSkippedWithoutCommit()
    {
        var gap = Gap("Widget");
        var worktreeManager = new FakeWorktreeManager(_session);
        var committer = new FakeGitCommitter();
        var reporter = new FakeSummaryReporter();
        var testGenerator = new FakeTestGenerator(FilePathFor);
        var buildRunner = new FakeBuildTestRunner([Fail("CS1002"), Fail("CS1002")], []);

        var runner = new CoverageCompletionRunner(
            worktreeManager, new FakeCoverageAnalyzer([gap]), buildRunner, committer, reporter, testGenerator,
            new RunnerOptions(MaxAttempts: 2));

        var exitCode = await runner.RunAsync(_repoPath, _solutionPath, CancellationToken.None);

        exitCode.Should().Be(0);
        reporter.Skipped.Should().ContainSingle();
        reporter.Skipped[0].Gap.Should().Be(gap);
        reporter.Skipped[0].Reason.Should().Contain("build failed after 2 attempts");
        reporter.Completed.Should().BeEmpty();
        committer.Commits.Should().BeEmpty();
        buildRunner.BuildCallCount.Should().Be(2);
        worktreeManager.RemoveCallCount.Should().Be(1);
    }

    [Fact]
    public async Task MultiGap_OneSucceedsOneExhausts_BothReportedAndWorktreeRemovedOnce()
    {
        var succeeding = Gap("Widget");
        var failing = Gap("Gadget");
        var worktreeManager = new FakeWorktreeManager(_session);
        var committer = new FakeGitCommitter();
        var reporter = new FakeSummaryReporter();
        var testGenerator = new FakeTestGenerator(FilePathFor);
        // Call order follows gap order: succeeding gap's build+test, then failing gap's two failed build attempts.
        var buildRunner = new FakeBuildTestRunner([Ok(), Fail(), Fail()], [Ok()]);

        var runner = new CoverageCompletionRunner(
            worktreeManager, new FakeCoverageAnalyzer([succeeding, failing]), buildRunner, committer, reporter,
            testGenerator, new RunnerOptions(MaxAttempts: 2));

        var exitCode = await runner.RunAsync(_repoPath, _solutionPath, CancellationToken.None);

        exitCode.Should().Be(0);
        reporter.Completed.Should().ContainSingle().Which.Gap.Should().Be(succeeding);
        reporter.Skipped.Should().ContainSingle().Which.Gap.Should().Be(failing);
        committer.Commits.Should().ContainSingle();
        worktreeManager.RemoveCallCount.Should().Be(1);
        reporter.WriteCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_MidLoop_StopsProcessingButStillWritesSummaryAndRemovesWorktree()
    {
        var succeeding = Gap("Widget");
        var interrupted = Gap("Gadget");
        var worktreeManager = new FakeWorktreeManager(_session);
        var committer = new FakeGitCommitter();
        var reporter = new FakeSummaryReporter();
        var testGenerator = new FakeTestGenerator(FilePathFor);
        using var cts = new CancellationTokenSource();

        // First gap builds fine; the second gap's build call is where the user hits Ctrl+C.
        var buildRunner = new FakeBuildTestRunner(
            [Ok(), () =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }],
            [Ok()]);

        var runner = new CoverageCompletionRunner(
            worktreeManager, new FakeCoverageAnalyzer([succeeding, interrupted]), buildRunner, committer, reporter,
            testGenerator);

        var exitCode = await runner.RunAsync(_repoPath, _solutionPath, cts.Token);

        exitCode.Should().Be(130);
        reporter.Completed.Should().ContainSingle().Which.Gap.Should().Be(succeeding);
        reporter.Skipped.Should().ContainSingle();
        reporter.Skipped[0].Gap.Should().Be(interrupted);
        reporter.Skipped[0].Reason.Should().Contain("cancelled");
        committer.Commits.Should().ContainSingle();
        worktreeManager.RemoveCallCount.Should().Be(1);
        reporter.WriteCallCount.Should().Be(1);
    }
}
