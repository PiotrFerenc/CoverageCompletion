using System.Diagnostics;

namespace CoverageCompletion.Infrastructure.Tests;

/// <summary>
/// Minimal synchronous git runner used only by tests, to set up/inspect real temp repos
/// without depending on the (internal, async) production ProcessRunner.
/// </summary>
internal static class GitCli
{
    public static string Run(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        }

        return stdout.Trim();
    }
}
