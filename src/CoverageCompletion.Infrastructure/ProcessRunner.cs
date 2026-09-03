using System.Diagnostics;
using System.Text;

namespace CoverageCompletion.Infrastructure;

/// <summary>
/// Result of running an external process: exit code plus separately captured stdout/stderr.
/// </summary>
internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => StandardOutput + StandardError;
}

/// <summary>
/// Thin wrapper around <see cref="Process"/> used by every Infrastructure class that shells
/// out to git/dotnet. Arguments are passed via <see cref="ProcessStartInfo.ArgumentList"/> so
/// callers never have to hand-quote paths or messages containing spaces.
/// </summary>
internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Target solutions may target an SDK/runtime major version not installed in this
        // environment (e.g. net8.0 apps where only 6/7/10 runtimes are present); roll forward
        // so `dotnet build`/`dotnet test` can still launch regardless of the host's exact runtimes.
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
