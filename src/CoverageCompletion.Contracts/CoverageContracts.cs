namespace CoverageCompletion.Contracts;

public record CoverageGap(
    string ProjectPath,
    string FilePath,
    string Namespace,
    string TypeName,
    string MemberName,
    IReadOnlyList<int> UncoveredLines);

public record WorktreeSession(string RepoPath, string WorktreePath, string BranchName, string BaseBranch);

public interface IWorktreeManager
{
    Task<WorktreeSession> CreateAsync(string repoPath, CancellationToken ct);

    Task RemoveAsync(WorktreeSession session, CancellationToken ct);
}

/// <summary>
/// Result of merging a finished session branch into a new branch cut from the branch the
/// session started on. <see cref="HasConflicts"/> true means <see cref="TargetWorktreePath"/>
/// was deliberately left in place (not cleaned up) with the conflict markers, for the user to
/// resolve by hand.
/// </summary>
public record MergeOutcome(string TargetBranch, string TargetWorktreePath, bool HasConflicts, string Output);

public interface IBranchMerger
{
    /// <summary>
    /// Merges one or more finished session branches, in order, into a single new branch cut
    /// from <paramref name="baseBranch"/>. If a branch conflicts, merging stops there and the
    /// target worktree - containing whatever merged cleanly plus the conflicted merge in
    /// progress - is left in place for manual resolution.
    /// </summary>
    Task<MergeOutcome> MergeSessionsIntoNewBranchAsync(
        string repoPath, string baseBranch, IReadOnlyList<string> sessionBranches, CancellationToken ct);
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
    Task<string> CommitFilesAsync(string worktreePath, IReadOnlyList<string> relativeFilePaths, string message, CancellationToken ct);
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
    /// <summary>
    /// Ensures the test project nearest <paramref name="testFilePath"/> has the required NuGet
    /// packages. Returns the path to the .csproj it modified (so the caller can commit that
    /// change alongside the generated test file), or null if nothing needed to change.
    /// </summary>
    Task<string?> EnsureRequiredPackagesAsync(string testFilePath, CancellationToken ct);
}
