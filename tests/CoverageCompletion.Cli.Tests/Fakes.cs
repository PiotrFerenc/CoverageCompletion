using CoverageCompletion.Contracts;

namespace CoverageCompletion.Cli.Tests;

/// <summary>
/// Hand-written in-memory fakes for the six Contracts interfaces. No mocking framework:
/// Contracts has no dependency on one, and these interfaces are small enough that fakes
/// read more clearly than mock setup code.
/// </summary>
public sealed class FakeWorktreeManager(WorktreeSession session) : IWorktreeManager
{
    public int RemoveCallCount { get; private set; }

    public Task<WorktreeSession> CreateAsync(string repoPath, CancellationToken ct) => Task.FromResult(session);

    public Task RemoveAsync(WorktreeSession removedSession, CancellationToken ct)
    {
        RemoveCallCount++;
        return Task.CompletedTask;
    }
}

public sealed class FakeCoverageAnalyzer(IReadOnlyList<CoverageGap> gaps) : ICoverageAnalyzer
{
    public Task<IReadOnlyList<CoverageGap>> AnalyzeAsync(string solutionPath, CancellationToken ct) =>
        Task.FromResult(gaps);
}

public sealed class FakeBuildTestRunner(
    IEnumerable<Func<BuildTestResult>> buildResults,
    IEnumerable<Func<BuildTestResult>> testResults) : IBuildTestRunner
{
    private readonly Queue<Func<BuildTestResult>> _buildResults = new(buildResults);
    private readonly Queue<Func<BuildTestResult>> _testResults = new(testResults);

    public int BuildCallCount { get; private set; }
    public int TestCallCount { get; private set; }

    public Task<BuildTestResult> BuildAsync(string worktreePath, CancellationToken ct)
    {
        BuildCallCount++;
        return Task.FromResult(Dequeue(_buildResults, "build")());
    }

    public Task<BuildTestResult> RunTestsAsync(string worktreePath, string testFilter, CancellationToken ct)
    {
        TestCallCount++;
        return Task.FromResult(Dequeue(_testResults, "test")());
    }

    private static Func<BuildTestResult> Dequeue(Queue<Func<BuildTestResult>> queue, string label) =>
        queue.Count > 0 ? queue.Dequeue() : throw new InvalidOperationException($"No more scripted {label} results.");
}

public sealed class FakeGitCommitter : IGitCommitter
{
    public List<(string WorktreePath, string RelativeFilePath, string Message)> Commits { get; } = [];

    public Task<string> CommitFileAsync(string worktreePath, string relativeFilePath, string message, CancellationToken ct)
    {
        Commits.Add((worktreePath, relativeFilePath, message));
        return Task.FromResult($"sha-{Commits.Count}");
    }
}

public sealed class FakeSummaryReporter : ISummaryReporter
{
    public List<(CoverageGap Gap, string Sha)> Completed { get; } = [];
    public List<(CoverageGap Gap, string Reason)> Skipped { get; } = [];
    public int WriteCallCount { get; private set; }
    public string? WrittenPath { get; private set; }

    public void RecordCompleted(CoverageGap gap, string commitSha) => Completed.Add((gap, commitSha));

    public void RecordSkipped(CoverageGap gap, string reason) => Skipped.Add((gap, reason));

    public Task WriteAsync(string path, CancellationToken ct)
    {
        WriteCallCount++;
        WrittenPath = path;
        return Task.CompletedTask;
    }
}

public sealed class FakeTestGenerator(Func<CoverageGap, string> filePathForGap) : ITestGenerator
{
    public int RegenerateCallCount { get; private set; }

    public Task<GeneratedTest> GenerateAsync(CoverageGap gap, string solutionPath, CancellationToken ct) =>
        Task.FromResult(new GeneratedTest(filePathForGap(gap), $"public class {gap.TypeName}Tests {{ }}"));

    public Task<GeneratedTest> RegenerateAsync(CoverageGap gap, GeneratedTest previous, string buildError, CancellationToken ct)
    {
        RegenerateCallCount++;
        return Task.FromResult(previous with { Content = previous.Content + "\n// regenerated" });
    }
}
