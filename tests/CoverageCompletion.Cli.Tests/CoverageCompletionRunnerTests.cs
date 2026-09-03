using CoverageCompletion.Contracts;
using Shouldly;

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
        _session = new WorktreeSession(_repoPath, _worktreePath, "coverage/branch-1", "main");
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

        exitCode.ShouldBe(0);
        reporter.Completed.ShouldHaveSingleItem().Gap.ShouldBe(gap);
        reporter.Skipped.ShouldBeEmpty();
        committer.Commits.ShouldHaveSingleItem();
        testGenerator.RegenerateCallCount.ShouldBe(0);
        worktreeManager.RemoveCallCount.ShouldBe(1);
        reporter.WriteCallCount.ShouldBe(1);
        File.Exists(FilePathFor(gap)).ShouldBeTrue();
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

        exitCode.ShouldBe(0);
        testGenerator.RegenerateCallCount.ShouldBe(1);
        buildRunner.BuildCallCount.ShouldBe(2);
        committer.Commits.ShouldHaveSingleItem();
        reporter.Completed.ShouldHaveSingleItem();
        reporter.Skipped.ShouldBeEmpty();
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

        exitCode.ShouldBe(0);
        reporter.Skipped.ShouldHaveSingleItem();
        reporter.Skipped[0].Gap.ShouldBe(gap);
        reporter.Skipped[0].Reason.ShouldContain("build failed after 2 attempts");
        reporter.Completed.ShouldBeEmpty();
        committer.Commits.ShouldBeEmpty();
        buildRunner.BuildCallCount.ShouldBe(2);
        worktreeManager.RemoveCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GenerationThrows_RecordsSkippedWithoutCrashing_AndStillCleansUp()
    {
        // Regression test: a real run against a Mediator-based fixture with a since-revoked API
        // key crashed the whole process with an unhandled HttpRequestException from OpenAiClient -
        // build/test failures were already handled via retry/skip, but a failure in generation
        // itself (bad key, quota, model refusal) propagated straight out of RunAsync.
        var gap = Gap("Widget");
        var worktreeManager = new FakeWorktreeManager(_session);
        var committer = new FakeGitCommitter();
        var reporter = new FakeSummaryReporter();
        var testGenerator = new FakeTestGenerator(FilePathFor, throwOnGenerate: new HttpRequestException("401 Unauthorized"));
        var buildRunner = new FakeBuildTestRunner([], []);

        var runner = new CoverageCompletionRunner(
            worktreeManager, new FakeCoverageAnalyzer([gap]), buildRunner, committer, reporter, testGenerator);

        var exitCode = await runner.RunAsync(_repoPath, _solutionPath, CancellationToken.None);

        exitCode.ShouldBe(0);
        reporter.Skipped.ShouldHaveSingleItem();
        reporter.Skipped[0].Gap.ShouldBe(gap);
        reporter.Skipped[0].Reason.ShouldContain("401 Unauthorized");
        reporter.Completed.ShouldBeEmpty();
        committer.Commits.ShouldBeEmpty();
        buildRunner.BuildCallCount.ShouldBe(0);
        worktreeManager.RemoveCallCount.ShouldBe(1);
        reporter.WriteCallCount.ShouldBe(1);
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

        exitCode.ShouldBe(0);
        reporter.Completed.ShouldHaveSingleItem().Gap.ShouldBe(succeeding);
        reporter.Skipped.ShouldHaveSingleItem().Gap.ShouldBe(failing);
        committer.Commits.ShouldHaveSingleItem();
        worktreeManager.RemoveCallCount.ShouldBe(1);
        reporter.WriteCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task PackageEnsurer_WhenProvided_IsCalledOncePerGapWithGeneratedFilePathBeforeFirstBuild()
    {
        var gap = Gap("Widget");
        var worktreeManager = new FakeWorktreeManager(_session);
        var committer = new FakeGitCommitter();
        var reporter = new FakeSummaryReporter();
        var testGenerator = new FakeTestGenerator(FilePathFor);
        var order = new List<string>();
        var packageEnsurer = new FakeTestProjectPackageEnsurer(_ => order.Add("ensure"));
        var buildRunner = new FakeBuildTestRunner(
            [() => { order.Add("build"); return new BuildTestResult(true, "ok"); }],
            [() => { order.Add("test"); return new BuildTestResult(true, "ok"); }]);

        var runner = new CoverageCompletionRunner(
            worktreeManager, new FakeCoverageAnalyzer([gap]), buildRunner, committer, reporter, testGenerator,
            packageEnsurer: packageEnsurer);

        var exitCode = await runner.RunAsync(_repoPath, _solutionPath, CancellationToken.None);

        exitCode.ShouldBe(0);
        packageEnsurer.EnsuredFilePaths.ShouldHaveSingleItem().ShouldBe(FilePathFor(gap));
        order.ShouldBe(["ensure", "build", "test"]);
    }

    [Fact]
    public async Task PackageEnsurer_WhenItModifiesCsproj_CommitsCsprojAlongsideGeneratedTestFile()
    {
        // Regression test: a real-key end-to-end run showed the package ensurer's csproj edit
        // (adding the required test packages) only ever existed in the ephemeral worktree - the
        // commit only ever included the generated test file, so a fresh checkout of that commit
        // failed to build. The ensurer's return value must now flow into the same commit.
        var gap = Gap("Widget");
        var worktreeManager = new FakeWorktreeManager(_session);
        var committer = new FakeGitCommitter();
        var reporter = new FakeSummaryReporter();
        var testGenerator = new FakeTestGenerator(FilePathFor);
        var csprojPath = Path.Combine(_worktreePath, "Tests", "Tests.csproj");
        var packageEnsurer = new FakeTestProjectPackageEnsurer(modifiedCsprojPath: csprojPath);
        var buildRunner = new FakeBuildTestRunner([Ok()], [Ok()]);

        var runner = new CoverageCompletionRunner(
            worktreeManager, new FakeCoverageAnalyzer([gap]), buildRunner, committer, reporter, testGenerator,
            packageEnsurer: packageEnsurer);

        var exitCode = await runner.RunAsync(_repoPath, _solutionPath, CancellationToken.None);

        exitCode.ShouldBe(0);
        committer.Commits.ShouldHaveSingleItem();
        committer.Commits[0].RelativeFilePaths.ShouldBe(
            [Path.GetRelativePath(_worktreePath, FilePathFor(gap)), Path.GetRelativePath(_worktreePath, csprojPath)],
            ignoreOrder: true);
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

        exitCode.ShouldBe(130);
        reporter.Completed.ShouldHaveSingleItem().Gap.ShouldBe(succeeding);
        reporter.Skipped.ShouldHaveSingleItem();
        reporter.Skipped[0].Gap.ShouldBe(interrupted);
        reporter.Skipped[0].Reason.ShouldContain("cancelled");
        committer.Commits.ShouldHaveSingleItem();
        worktreeManager.RemoveCallCount.ShouldBe(1);
        reporter.WriteCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task BranchMergerProvided_MergesSessionBranchIntoNewBranchFromBase_BeforeRemovingTheWorktree()
    {
        var gap = Gap("Widget");
        var worktreeManager = new FakeWorktreeManager(_session);
        var committer = new FakeGitCommitter();
        var reporter = new FakeSummaryReporter();
        var testGenerator = new FakeTestGenerator(FilePathFor);
        var buildRunner = new FakeBuildTestRunner([Ok()], [Ok()]);
        var branchMerger = new FakeBranchMerger(new MergeOutcome("coverage/merged-1", "/tmp/whatever", HasConflicts: false, "Fast-forward"));

        var runner = new CoverageCompletionRunner(
            worktreeManager, new FakeCoverageAnalyzer([gap]), buildRunner, committer, reporter, testGenerator,
            branchMerger: branchMerger);

        var exitCode = await runner.RunAsync(_repoPath, _solutionPath, CancellationToken.None);

        exitCode.ShouldBe(0);
        branchMerger.Calls.ShouldHaveSingleItem().ShouldBe((_repoPath, _session.BaseBranch, _session.BranchName));
    }
}
