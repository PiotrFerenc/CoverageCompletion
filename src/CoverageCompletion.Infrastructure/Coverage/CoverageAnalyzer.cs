using CoverageCompletion.Contracts;

namespace CoverageCompletion.Infrastructure.Coverage;

/// <summary>
/// Runs `dotnet test --collect:"XPlat Code Coverage"` against a solution and parses the
/// coverlet-produced Cobertura report into <see cref="CoverageGap"/>s.
/// </summary>
public sealed class CoverageAnalyzer : ICoverageAnalyzer
{
    public async Task<IReadOnlyList<CoverageGap>> AnalyzeAsync(string solutionPath, CancellationToken ct)
    {
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))
            ?? throw new InvalidOperationException($"Could not resolve directory for solution path '{solutionPath}'.");

        // Wipe stale TestResults so the coverage file we pick up below is guaranteed fresh.
        foreach (var staleDir in Directory.GetDirectories(solutionDir, "TestResults", SearchOption.AllDirectories))
        {
            Directory.Delete(staleDir, recursive: true);
        }

        var result = await ProcessRunner.RunAsync(
            "dotnet",
            ["test", solutionPath, "--collect:XPlat Code Coverage"],
            solutionDir,
            ct);

        // coverlet.collector's "XPlat Code Coverage" collector emits Cobertura XML by default,
        // so no extra conversion step (e.g. reportgenerator) is needed.
        var coverageFile = Directory.GetFiles(solutionDir, "coverage.cobertura.xml", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (coverageFile is null)
        {
            throw new InvalidOperationException(
                $"'dotnet test --collect:\"XPlat Code Coverage\"' did not produce a coverage.cobertura.xml file under '{solutionDir}'. Output:\n{result.CombinedOutput}");
        }

        var xml = await File.ReadAllTextAsync(coverageFile, ct);
        var gaps = CoberturaCoverageParser.Parse(xml);

        return gaps
            .Select(gap => gap with { ProjectPath = FindNearestCsproj(gap.FilePath, solutionDir) })
            .ToList();
    }

    private static string FindNearestCsproj(string filePath, string fallbackDir)
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

        return fallbackDir;
    }
}
