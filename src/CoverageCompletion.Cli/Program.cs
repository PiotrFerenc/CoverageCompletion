using System.Text.RegularExpressions;
using CoverageCompletion.Contracts;
using CoverageCompletion.Generation;
using CoverageCompletion.Infrastructure.Build;
using CoverageCompletion.Infrastructure.Coverage;
using CoverageCompletion.Infrastructure.Git;
using CoverageCompletion.Infrastructure.Reporting;
using Microsoft.Extensions.DependencyInjection;

const int maxAttempts = 5;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: CoverageCompletion.Cli <path-to-solution.sln>");
    return 1;
}

var solutionPath = Path.GetFullPath(args[0]);
if (!File.Exists(solutionPath))
{
    Console.Error.WriteLine($"Solution file not found: {solutionPath}");
    return 1;
}

var repoPath = FindRepoRoot(Path.GetDirectoryName(solutionPath)!);
if (repoPath is null)
{
    Console.Error.WriteLine("Solution is not inside a git repository.");
    return 1;
}

var services = new ServiceCollection();
services.AddHttpClient<OpenAiClient>();
services.AddSingleton<IWorktreeManager, WorktreeManager>();
services.AddSingleton<ICoverageAnalyzer, CoverageAnalyzer>();
services.AddSingleton<IBuildTestRunner, BuildTestRunner>();
services.AddSingleton<IGitCommitter, GitCommitter>();
services.AddSingleton<ISummaryReporter, SummaryReporter>();
services.AddSingleton<TestPatternFinder>();
services.AddSingleton<PromptBuilder>();
services.AddSingleton<ITestGenerator, TestGenerator>();

await using var provider = services.BuildServiceProvider();

var worktreeManager = provider.GetRequiredService<IWorktreeManager>();
var coverageAnalyzer = provider.GetRequiredService<ICoverageAnalyzer>();
var buildRunner = provider.GetRequiredService<IBuildTestRunner>();
var committer = provider.GetRequiredService<IGitCommitter>();
var reporter = provider.GetRequiredService<ISummaryReporter>();
var testGenerator = provider.GetRequiredService<ITestGenerator>();

var ct = CancellationToken.None;

Console.WriteLine($"Creating worktree session for {repoPath}...");
var session = await worktreeManager.CreateAsync(repoPath, ct);
Console.WriteLine($"Worktree ready: {session.WorktreePath} (branch {session.BranchName})");

var solutionRelativePath = Path.GetRelativePath(repoPath, solutionPath);
var worktreeSolutionPath = Path.Combine(session.WorktreePath, solutionRelativePath);

try
{
    Console.WriteLine("Analyzing coverage...");
    var gaps = await coverageAnalyzer.AnalyzeAsync(worktreeSolutionPath, ct);
    Console.WriteLine($"Found {gaps.Count} coverage gap(s).");

    foreach (var gap in gaps)
    {
        Console.WriteLine($"--- {gap.TypeName}.{gap.MemberName} ---");
        var generated = await testGenerator.GenerateAsync(gap, worktreeSolutionPath, ct);
        var completed = false;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(generated.FilePath)!);
            await File.WriteAllTextAsync(generated.FilePath, generated.Content, ct);

            var build = await buildRunner.BuildAsync(session.WorktreePath, ct);
            if (!build.Success)
            {
                if (attempt == maxAttempts)
                {
                    reporter.RecordSkipped(gap, $"build failed after {maxAttempts} attempts: {Truncate(build.Output)}");
                    break;
                }

                generated = await testGenerator.RegenerateAsync(gap, generated, build.Output, ct);
                continue;
            }

            var testClassName = ExtractClassName(generated.Content) ?? Path.GetFileNameWithoutExtension(generated.FilePath);
            var test = await buildRunner.RunTestsAsync(session.WorktreePath, $"FullyQualifiedName~{testClassName}", ct);
            if (!test.Success)
            {
                if (attempt == maxAttempts)
                {
                    reporter.RecordSkipped(gap, $"tests failed after {maxAttempts} attempts: {Truncate(test.Output)}");
                    break;
                }

                generated = await testGenerator.RegenerateAsync(gap, generated, test.Output, ct);
                continue;
            }

            var relativeFilePath = Path.GetRelativePath(session.WorktreePath, generated.FilePath);
            var sha = await committer.CommitFileAsync(
                session.WorktreePath, relativeFilePath, $"test: cover {gap.TypeName}.{gap.MemberName}", ct);
            reporter.RecordCompleted(gap, sha);
            completed = true;
            break;
        }

        Console.WriteLine(completed ? "  -> committed" : "  -> skipped");
    }
}
finally
{
    var summaryPath = Path.Combine(repoPath, $"coverage-completion-summary-{session.BranchName.Replace('/', '-')}.md");
    await reporter.WriteAsync(summaryPath, ct);
    Console.WriteLine($"Summary written to {summaryPath}");

    await worktreeManager.RemoveAsync(session, ct);
    Console.WriteLine("Worktree removed.");
}

return 0;

static string? FindRepoRoot(string startDir)
{
    var dir = new DirectoryInfo(startDir);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    return null;
}

static string? ExtractClassName(string testCode)
{
    var match = Regex.Match(testCode, @"class\s+(\w+)");
    return match.Success ? match.Groups[1].Value : null;
}

static string Truncate(string text, int max = 2000) => text.Length <= max ? text : text[..max] + "... (truncated)";
