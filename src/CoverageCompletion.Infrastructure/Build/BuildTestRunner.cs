using CoverageCompletion.Contracts;

namespace CoverageCompletion.Infrastructure.Build;

/// <summary>
/// Runs `dotnet build` / `dotnet test --filter` in a worktree and reports success + combined output.
/// </summary>
public sealed class BuildTestRunner : IBuildTestRunner
{
    public async Task<BuildTestResult> BuildAsync(string worktreePath, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("dotnet", ["build"], worktreePath, ct);
        return new BuildTestResult(result.ExitCode == 0, result.CombinedOutput);
    }

    public async Task<BuildTestResult> RunTestsAsync(string worktreePath, string testFilter, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("dotnet", ["test", "--filter", testFilter], worktreePath, ct);
        return new BuildTestResult(result.ExitCode == 0, result.CombinedOutput);
    }
}
