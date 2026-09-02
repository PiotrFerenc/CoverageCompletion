using System.Text.RegularExpressions;
using CoverageCompletion.Contracts;

namespace CoverageCompletion.Generation;

/// <summary>
/// Locates an existing test file in the target solution to use as a style example
/// (assertion conventions, mocking style, etc.) for LLM-generated tests.
/// </summary>
public class TestPatternFinder
{
    private static readonly string[] TestDirSuffixes = { ".Tests", ".UnitTests", ".IntegrationTests" };
    private static readonly string[] TestPackageIds = { "xunit", "nunit", "mstest" };

    // Matches an identifier ending in "Handler" (e.g. OrderHandler, IRequestHandler) as a whole
    // token, not just the substring "Handler" appearing anywhere (e.g. inside "HandlerTests").
    private static readonly Regex HandlerIdentifierPattern = new(@"\b\w*Handler\b", RegexOptions.Compiled);

    /// <summary>
    /// Finds an example test file's content for the given coverage gap, or null if none found.
    /// Strategy 1: naming convention {TypeName}Tests.cs / {TypeName}Test.cs.
    /// Strategy 2 (fallback): first test file where ALL of the following co-occur:
    ///   (a) an xUnit test signal ([Fact] or [Theory]),
    ///   (b) a Result-style assertion signal (.IsSuccess, .IsFailed, or FluentResults), and
    ///   (c) a Mediator handler signal (an identifier ending in "Handler").
    /// </summary>
    public string? FindExampleTest(CoverageGap gap, string solutionPath)
    {
        var testDirs = FindTestProjectDirectories(solutionPath);
        if (testDirs.Count == 0)
        {
            return null;
        }

        var candidateNames = new[] { $"{gap.TypeName}Tests.cs", $"{gap.TypeName}Test.cs" };
        foreach (var dir in testDirs)
        {
            foreach (var name in candidateNames)
            {
                var match = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
                if (match is not null)
                {
                    return File.ReadAllText(match);
                }
            }
        }

        foreach (var dir in testDirs)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var content = File.ReadAllText(file);
                var hasXunitTestSignal = content.Contains("[Fact]") || content.Contains("[Theory]");
                var hasResultAssertionSignal = content.Contains(".IsSuccess") || content.Contains(".IsFailed") ||
                    content.Contains("FluentResults");
                var hasHandlerSignal = HandlerIdentifierPattern.IsMatch(content);

                if (hasXunitTestSignal && hasResultAssertionSignal && hasHandlerSignal)
                {
                    return content;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Finds directories of test projects within the solution, identified either by directory
    /// name convention (*.Tests, *.UnitTests, *.IntegrationTests) or by a *.csproj referencing
    /// a known test framework package (xunit/nunit/mstest).
    /// </summary>
    public IReadOnlyList<string> FindTestProjectDirectories(string solutionPath)
    {
        if (!Directory.Exists(solutionPath))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();

        foreach (var csproj in Directory.EnumerateFiles(solutionPath, "*.csproj", SearchOption.AllDirectories))
        {
            var dir = Path.GetDirectoryName(csproj)!;
            var isByName = TestDirSuffixes.Any(suffix => dir.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            var isByPackageRef = !isByName && ContainsTestPackageReference(csproj);

            if (isByName || isByPackageRef)
            {
                result.Add(dir);
            }
        }

        return result;
    }

    private static bool ContainsTestPackageReference(string csprojPath)
    {
        var content = File.ReadAllText(csprojPath);
        return TestPackageIds.Any(id => content.Contains(id, StringComparison.OrdinalIgnoreCase));
    }
}
