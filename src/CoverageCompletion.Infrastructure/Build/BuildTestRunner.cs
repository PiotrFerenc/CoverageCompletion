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
        // --no-build: callers always call BuildAsync first, so `dotnet test` re-building from
        // scratch here would be a second full build for no reason - cuts every attempt's cost
        // roughly in half.
        var result = await ProcessRunner.RunAsync(
            "dotnet", ["test", "--no-build", "--filter", testFilter], worktreePath, ct);
        return new BuildTestResult(result.ExitCode == 0, result.CombinedOutput);
    }
}
