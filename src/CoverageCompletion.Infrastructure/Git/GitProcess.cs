namespace CoverageCompletion.Infrastructure.Git;

/// <summary>
/// Runs a git command and throws with the captured stderr when it fails. Shared by
/// <see cref="WorktreeManager"/> and <see cref="GitCommitter"/>.
/// </summary>
internal static class GitProcess
{
    public static async Task<string> RunOrThrowAsync(string workingDirectory, CancellationToken ct, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync("git", arguments, workingDirectory, ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return result.StandardOutput.Trim();
    }
}
