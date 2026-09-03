namespace CoverageCompletion.Contracts;

public record CoverageGap(
    string ProjectPath,
    string FilePath,
    string Namespace,
    string TypeName,
    string MemberName,
    IReadOnlyList<int> UncoveredLines);

public record WorktreeSession(string RepoPath, string WorktreePath, string BranchName);

public interface IWorktreeManager
{
    Task<WorktreeSession> CreateAsync(string repoPath, CancellationToken ct);

    Task RemoveAsync(WorktreeSession session, CancellationToken ct);
}

public interface ICoverageAnalyzer
{
    Task<IReadOnlyList<CoverageGap>> AnalyzeAsync(string solutionPath, CancellationToken ct);
}

public record BuildTestResult(bool Success, string Output);

public interface IBuildTestRunner
{
    Task<BuildTestResult> BuildAsync(string worktreePath, CancellationToken ct);

    Task<BuildTestResult> RunTestsAsync(string worktreePath, string testFilter, CancellationToken ct);
}

public interface IGitCommitter
{
    Task<string> CommitFileAsync(string worktreePath, string relativeFilePath, string message, CancellationToken ct);
}

public record GeneratedTest(string FilePath, string Content);

public interface ITestGenerator
{
    Task<GeneratedTest> GenerateAsync(CoverageGap gap, string solutionPath, CancellationToken ct);

    Task<GeneratedTest> RegenerateAsync(CoverageGap gap, GeneratedTest previous, string buildError, CancellationToken ct);
}

public interface ISummaryReporter
{
    void RecordCompleted(CoverageGap gap, string commitSha);

    void RecordSkipped(CoverageGap gap, string reason);

    Task WriteAsync(string path, CancellationToken ct);
}

public interface ITestProjectPackageEnsurer
{
    Task EnsureRequiredPackagesAsync(string testFilePath, CancellationToken ct);
}
