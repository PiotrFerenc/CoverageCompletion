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

        // Source generators (e.g. Mediator's) can appear as <class> entries in the Cobertura
        // report with a filename Roslyn assigns them internally, even though no such file is
        // ever written to disk (EmitCompilerGeneratedFiles defaults to off). There's no real
        // source to read or write a test against for those, so drop them here rather than
        // crashing downstream when nothing at that path exists.
        return gaps
            .Where(gap => File.Exists(Path.GetFullPath(gap.FilePath, solutionDir)))
            .Select(gap => gap with { ProjectPath = FindNearestCsproj(gap.FilePath, solutionDir) })
            .ToList();
    }

    private static string FindNearestCsproj(string filePath, string fallbackDir)
    {
        // Cobertura's filename attribute is relative to the directory `dotnet test` ran in
        // (solutionDir here, passed in as fallbackDir), NOT to this process's own working
        // directory - Path.GetFullPath(filePath) alone resolves against Environment.CurrentDirectory,
        // which silently breaks whenever the calling process's cwd differs from solutionDir.
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath, fallbackDir));
        while (dir is not null)
        {
            // Belt-and-braces: callers are expected to only pass paths backed by a real file
            // (see the File.Exists filter above), but don't let a missing directory anywhere
            // in the walk-up turn into an unhandled crash regardless.
            var csproj = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.csproj").FirstOrDefault() : null;
            if (csproj is not null)
            {
                return csproj;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return fallbackDir;
    }
}
