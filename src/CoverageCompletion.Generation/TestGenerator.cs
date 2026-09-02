using CoverageCompletion.Contracts;

namespace CoverageCompletion.Generation;

/// <summary>
/// Generates missing unit tests for coverage gaps via an LLM, using an example test from
/// the target solution (when found) to match its existing assertion/mocking style.
/// </summary>
public class TestGenerator : ITestGenerator
{
    private static readonly string[] TestDirSuffixes = { ".Tests", ".UnitTests", ".IntegrationTests" };

    private readonly TestPatternFinder _patternFinder;
    private readonly PromptBuilder _promptBuilder;
    private readonly OpenAiClient _openAiClient;

    public TestGenerator(TestPatternFinder patternFinder, PromptBuilder promptBuilder, OpenAiClient openAiClient)
    {
        _patternFinder = patternFinder;
        _promptBuilder = promptBuilder;
        _openAiClient = openAiClient;
    }

    public async Task<GeneratedTest> GenerateAsync(CoverageGap gap, string solutionPath, CancellationToken ct)
    {
        var sourceCode = await File.ReadAllTextAsync(gap.FilePath, ct);
        var exampleTest = _patternFinder.FindExampleTest(gap, solutionPath);
        var prompt = _promptBuilder.BuildInitialPrompt(gap, sourceCode, exampleTest);
        var generatedCode = await _openAiClient.CompleteAsync(prompt, ct);

        return new GeneratedTest(BuildTestFilePath(gap, solutionPath), generatedCode);
    }

    public async Task<GeneratedTest> RegenerateAsync(CoverageGap gap, GeneratedTest previous, string buildError, CancellationToken ct)
    {
        var prompt = _promptBuilder.BuildRegenerationPrompt(gap, previous, buildError);
        var generatedCode = await _openAiClient.CompleteAsync(prompt, ct);

        return new GeneratedTest(previous.FilePath, generatedCode);
    }

    private string BuildTestFilePath(CoverageGap gap, string solutionPath)
    {
        var testDir = FindClosestTestDirectory(gap, solutionPath) ?? Path.GetDirectoryName(gap.ProjectPath) ?? solutionPath;
        var path = Path.Combine(testDir, $"{gap.TypeName}Tests.cs");

        if (!File.Exists(path))
        {
            return path;
        }

        var suffix = 2;
        string candidate;
        do
        {
            candidate = Path.Combine(testDir, $"{gap.TypeName}Tests{suffix}.cs");
            suffix++;
        }
        while (File.Exists(candidate));

        return candidate;
    }

    private string? FindClosestTestDirectory(CoverageGap gap, string solutionPath)
    {
        var candidates = _patternFinder.FindTestProjectDirectories(solutionPath)
            .Where(dir => TestDirSuffixes.Any(suffix => dir.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var sourceDir = Path.GetDirectoryName(gap.ProjectPath) ?? gap.ProjectPath;
        return candidates
            .OrderByDescending(dir => CommonPrefixLength(dir, sourceDir))
            .First();
    }

    private static int CommonPrefixLength(string a, string b)
    {
        var max = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < max && a[i] == b[i])
        {
            i++;
        }

        return i;
    }
}
