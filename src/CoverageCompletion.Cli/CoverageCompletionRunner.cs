using System.Text.RegularExpressions;
using CoverageCompletion.Contracts;

namespace CoverageCompletion.Cli;

public sealed record RunnerOptions(int MaxAttempts = 5);

/// <summary>
/// Orchestrates one coverage-completion session: worktree -> analyze -> per-gap
/// generate/build/test/retry/commit -> summary -> worktree removal. Extracted out of
/// Program.cs so it can be unit tested against fakes of the six Contracts interfaces,
/// independently of process startup / DI / console wiring.
/// </summary>
public sealed class CoverageCompletionRunner(
    IWorktreeManager worktreeManager,
    ICoverageAnalyzer coverageAnalyzer,
    IBuildTestRunner buildRunner,
    IGitCommitter committer,
    ISummaryReporter reporter,
    ITestGenerator testGenerator,
    RunnerOptions? options = null,
    ITestProjectPackageEnsurer? packageEnsurer = null,
    IBranchMerger? branchMerger = null)
{
    private readonly RunnerOptions _options = options ?? new RunnerOptions();

    public async Task<int> RunAsync(string repoPath, string solutionPath, CancellationToken ct)
    {
        WorktreeSession session;
        try
        {
            session = await worktreeManager.CreateAsync(repoPath, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // ponytail: nothing to clean up here, no worktree/session exists yet, so no
            // finally-block work applies. Just exit cleanly instead of letting an
            // unhandled OperationCanceledException crash the process.
            Console.WriteLine("Cancelled before the worktree was created.");
            return 130;
        }

        Console.WriteLine($"Worktree ready: {session.WorktreePath} (branch {session.BranchName})");

        var solutionRelativePath = Path.GetRelativePath(repoPath, solutionPath);
        var worktreeSolutionPath = Path.Combine(session.WorktreePath, solutionRelativePath);

        try
        {
            Console.WriteLine("Analyzing coverage...");
            IReadOnlyList<CoverageGap> gaps;
            try
            {
                gaps = await coverageAnalyzer.AnalyzeAsync(worktreeSolutionPath, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Console.WriteLine("Cancelled before any gap was processed.");
                return 130;
            }

            Console.WriteLine($"Found {gaps.Count} coverage gap(s).");

            foreach (var gap in gaps)
            {
                if (ct.IsCancellationRequested)
                {
                    Console.WriteLine("Cancellation requested, stopping before further gaps.");
                    break;
                }

                Console.WriteLine($"--- {gap.TypeName}.{gap.MemberName} ---");

                try
                {
                    await ProcessGapAsync(gap, session, worktreeSolutionPath, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    Console.WriteLine("  -> cancelled mid-attempt, treated as skipped");
                    reporter.RecordSkipped(gap, "cancelled by user");
                    break;
                }
            }
        }
        finally
        {
            if (branchMerger is not null)
            {
                var mergeOutcome = await branchMerger.MergeSessionIntoNewBranchAsync(
                    repoPath, session.BaseBranch, session.BranchName, CancellationToken.None);

                Console.WriteLine(mergeOutcome.HasConflicts
                    ? $"Merge into {mergeOutcome.TargetBranch} has conflicts - resolve manually in {mergeOutcome.TargetWorktreePath}"
                    : $"Merged into {mergeOutcome.TargetBranch}");
            }

            var summaryPath = Path.Combine(repoPath, $"coverage-completion-summary-{session.BranchName.Replace('/', '-')}.md");
            await reporter.WriteAsync(summaryPath, CancellationToken.None);
            Console.WriteLine($"Summary written to {summaryPath}");

            await worktreeManager.RemoveAsync(session, CancellationToken.None);
            Console.WriteLine("Worktree removed.");
        }

        return ct.IsCancellationRequested ? 130 : 0;
    }

    private async Task ProcessGapAsync(CoverageGap gap, WorktreeSession session, string worktreeSolutionPath, CancellationToken ct)
    {
        // A failure here (bad/expired API key, quota exhausted, model rejects the request, etc.)
        // isn't something retrying the SAME call fixes, and it's not this gap's fault the way a
        // failing build/test is - but it also shouldn't crash the whole session over one gap. Treat
        // it the same as an exhausted retry: skip this gap, log why, keep going.
        var generated = await TryGenerateAsync(gap, () => testGenerator.GenerateAsync(gap, worktreeSolutionPath, ct));
        if (generated is null)
        {
            return;
        }

        string? modifiedCsprojPath = null;
        if (packageEnsurer is not null)
        {
            modifiedCsprojPath = await packageEnsurer.EnsureRequiredPackagesAsync(generated.FilePath, ct);
        }

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            Directory.CreateDirectory(Path.GetDirectoryName(generated.FilePath)!);
            await File.WriteAllTextAsync(generated.FilePath, generated.Content, ct);

            var build = await buildRunner.BuildAsync(session.WorktreePath, ct);
            if (!build.Success)
            {
                if (attempt == _options.MaxAttempts)
                {
                    reporter.RecordSkipped(gap, $"build failed after {_options.MaxAttempts} attempts: {Truncate(build.Output)}");
                    Console.WriteLine("  -> skipped");
                    return;
                }

                generated = await TryGenerateAsync(gap, () => testGenerator.RegenerateAsync(gap, generated, build.Output, ct));
                if (generated is null)
                {
                    return;
                }

                continue;
            }

            var testClassName = TestClassNameExtractor.Extract(generated.Content)
                ?? Path.GetFileNameWithoutExtension(generated.FilePath);
            var test = await buildRunner.RunTestsAsync(session.WorktreePath, $"FullyQualifiedName~{testClassName}", ct);
            if (!test.Success)
            {
                if (attempt == _options.MaxAttempts)
                {
                    reporter.RecordSkipped(gap, $"tests failed after {_options.MaxAttempts} attempts: {Truncate(test.Output)}");
                    Console.WriteLine("  -> skipped");
                    return;
                }

                generated = await TryGenerateAsync(gap, () => testGenerator.RegenerateAsync(gap, generated, test.Output, ct));
                if (generated is null)
                {
                    return;
                }

                continue;
            }

            var relativeFilePaths = new List<string> { Path.GetRelativePath(session.WorktreePath, generated.FilePath) };
            if (modifiedCsprojPath is not null)
            {
                // The package ensurer's csproj edit lives only in this worktree until it's
                // committed alongside the test file that actually needs it - otherwise the
                // generated test would fail to build on a fresh checkout of this commit.
                relativeFilePaths.Add(Path.GetRelativePath(session.WorktreePath, modifiedCsprojPath));
            }

            var sha = await committer.CommitFilesAsync(
                session.WorktreePath, relativeFilePaths, $"test: cover {gap.TypeName}.{gap.MemberName}", ct);
            reporter.RecordCompleted(gap, sha);
            Console.WriteLine("  -> committed");
            return;
        }
    }

    private async Task<GeneratedTest?> TryGenerateAsync(CoverageGap gap, Func<Task<GeneratedTest>> generate)
    {
        try
        {
            return await generate();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            reporter.RecordSkipped(gap, $"test generation failed: {ex.Message}");
            Console.WriteLine("  -> skipped (generation error)");
            return null;
        }
    }

    private static string Truncate(string text, int max = 2000) => text.Length <= max ? text : text[..max] + "... (truncated)";
}

/// <summary>
/// Extracts the primary test class name from generated C# source, used to build the
/// `dotnet test --filter` expression. Deliberately simple: takes the first type
/// declaration, which is what the generation prompt always asks for.
/// </summary>
public static class TestClassNameExtractor
{
    private static readonly Regex ClassDeclaration = new(
        @"\bclass\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    public static string? Extract(string testCode)
    {
        var match = ClassDeclaration.Match(testCode);
        return match.Success ? match.Groups["name"].Value : null;
    }
}
