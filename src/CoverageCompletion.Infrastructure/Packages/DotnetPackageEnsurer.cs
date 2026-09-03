using CoverageCompletion.Contracts;

namespace CoverageCompletion.Infrastructure.Packages;

/// <summary>
/// Makes sure the generated test's own project references the packages every generated test
/// assumes are available (FluentAssertions, NSubstitute) - the fixed test stack this tool
/// generates against. If the target solution's test project doesn't already have them, a
/// build failure isn't fixable by the LLM regenerate-on-error loop, so this runs once up
/// front instead.
/// </summary>
public sealed class DotnetPackageEnsurer : ITestProjectPackageEnsurer
{
    private static readonly string[] RequiredPackages = ["FluentAssertions", "NSubstitute"];

    public async Task<string?> EnsureRequiredPackagesAsync(string testFilePath, CancellationToken ct)
    {
        var csprojPath = FindNearestCsproj(testFilePath);
        var csprojContent = await File.ReadAllTextAsync(csprojPath, ct);
        var changed = false;

        foreach (var package in RequiredPackages)
        {
            if (csprojContent.Contains($"Include=\"{package}\"", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var result = await ProcessRunner.RunAsync(
                "dotnet", ["add", csprojPath, "package", package], Path.GetDirectoryName(csprojPath)!, ct);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"dotnet add {csprojPath} package {package} failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
            }

            changed = true;
        }

        return changed ? csprojPath : null;
    }

    private static string FindNearestCsproj(string filePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        while (dir is not null)
        {
            var csproj = Directory.GetFiles(dir, "*.csproj").FirstOrDefault();
            if (csproj is not null)
            {
                return csproj;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException($"No .csproj found above '{filePath}'.");
    }
}
