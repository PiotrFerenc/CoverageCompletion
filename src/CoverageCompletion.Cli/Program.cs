using System.Reflection;
using CoverageCompletion.Cli;
using CoverageCompletion.Contracts;
using CoverageCompletion.Generation;
using CoverageCompletion.Infrastructure.Build;
using CoverageCompletion.Infrastructure.Coverage;
using CoverageCompletion.Infrastructure.Git;
using CoverageCompletion.Infrastructure.Packages;
using CoverageCompletion.Infrastructure.Reporting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// OpenAiClient reads OPENAI_API_KEY/OPENAI_MODEL via Environment.GetEnvironmentVariable, so
// bridge them in from `dotnet user-secrets` too (local dev convenience) without touching that
// simple contract - an explicit env var, if already set, always wins.
var userSecrets = new ConfigurationBuilder().AddUserSecrets(Assembly.GetExecutingAssembly()).Build();
foreach (var key in new[] { "OPENAI_API_KEY", "OPENAI_MODEL" })
{
    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)) && !string.IsNullOrWhiteSpace(userSecrets[key]))
    {
        Environment.SetEnvironmentVariable(key, userSecrets[key]);
    }
}

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
services.AddSingleton<ITestProjectPackageEnsurer, DotnetPackageEnsurer>();
services.AddSingleton<TestPatternFinder>();
services.AddSingleton<PromptBuilder>();
services.AddSingleton<ITestGenerator, TestGenerator>();
services.AddSingleton<CoverageCompletionRunner>();

await using var provider = services.BuildServiceProvider();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    Console.WriteLine("Cancellation requested, finishing current attempt then stopping...");
    cts.Cancel();
};

var runner = provider.GetRequiredService<CoverageCompletionRunner>();

Console.WriteLine($"Creating worktree session for {repoPath}...");
return await runner.RunAsync(repoPath, solutionPath, cts.Token);

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
