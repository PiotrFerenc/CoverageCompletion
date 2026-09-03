using System.Net;
using CoverageCompletion.Contracts;
using CoverageCompletion.Generation;
using Shouldly;

namespace CoverageCompletion.Generation.Tests;

public class TestGeneratorTests : IDisposable
{
    private readonly string _solutionRoot;

    public TestGeneratorTests()
    {
        _solutionRoot = Path.Combine(Path.GetTempPath(), "CoverageCompletionGen_" + Guid.NewGuid());
        Directory.CreateDirectory(_solutionRoot);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
    }

    public void Dispose()
    {
        if (Directory.Exists(_solutionRoot))
        {
            Directory.Delete(_solutionRoot, recursive: true);
        }
    }

    private class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            const string json = """
                { "choices": [ { "message": { "content": "```csharp\npublic class GeneratedTests {}\n```" } } ] }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private static TestGenerator MakeGenerator() =>
        new(new TestPatternFinder(), new PromptBuilder(), new OpenAiClient(new HttpClient(new StubHandler())));

    [Fact]
    public async Task GenerateAsync_PlacesTestFileInClosestTestProject()
    {
        var srcDir = Path.Combine(_solutionRoot, "src", "Foo");
        Directory.CreateDirectory(srcDir);
        var sourceFile = Path.Combine(srcDir, "OrderHandler.cs");
        File.WriteAllText(sourceFile, "public class OrderHandler {}");

        var testsDir = Path.Combine(_solutionRoot, "tests", "Foo.Tests");
        Directory.CreateDirectory(testsDir);
        File.WriteAllText(Path.Combine(testsDir, "Foo.Tests.csproj"), "<Project></Project>");

        var gap = new CoverageGap(
            ProjectPath: Path.Combine(srcDir, "Foo.csproj"),
            FilePath: sourceFile,
            Namespace: "Foo",
            TypeName: "OrderHandler",
            MemberName: "Handle",
            UncoveredLines: new[] { 1 });

        var result = await MakeGenerator().GenerateAsync(gap, _solutionRoot, CancellationToken.None);

        result.FilePath.ShouldBe(Path.Combine(testsDir, "OrderHandlerTests.cs"));
        result.Content.ShouldBe("public class GeneratedTests {}");
    }

    [Fact]
    public async Task GenerateAsync_GivenSlnFilePathNotDirectory_StillPlacesTestFileInTestProject()
    {
        // Regression test: CoverageCompletionRunner always passes the .sln FILE path, not its
        // directory, as `solutionPath`. See TestPatternFinderTests for the root cause this guards
        // against (FindTestProjectDirectories used to require a directory and silently found
        // nothing for a file path, so this used to fall back to writing the generated test into
        // the SOURCE project's directory instead of the test project's).
        var srcDir = Path.Combine(_solutionRoot, "src", "Foo");
        Directory.CreateDirectory(srcDir);
        var sourceFile = Path.Combine(srcDir, "OrderHandler.cs");
        File.WriteAllText(sourceFile, "public class OrderHandler {}");

        var testsDir = Path.Combine(_solutionRoot, "tests", "Foo.Tests");
        Directory.CreateDirectory(testsDir);
        File.WriteAllText(Path.Combine(testsDir, "Foo.Tests.csproj"), "<Project></Project>");

        var slnPath = Path.Combine(_solutionRoot, "Solution.sln");
        File.WriteAllText(slnPath, "Microsoft Visual Studio Solution File, Format Version 12.00");

        var gap = new CoverageGap(
            ProjectPath: Path.Combine(srcDir, "Foo.csproj"),
            FilePath: sourceFile,
            Namespace: "Foo",
            TypeName: "OrderHandler",
            MemberName: "Handle",
            UncoveredLines: new[] { 1 });

        var result = await MakeGenerator().GenerateAsync(gap, slnPath, CancellationToken.None);

        result.FilePath.ShouldBe(Path.Combine(testsDir, "OrderHandlerTests.cs"));
    }

    [Fact]
    public async Task GenerateAsync_ExistingTestFile_AppendsSuffixToAvoidCollision()
    {
        var srcDir = Path.Combine(_solutionRoot, "src", "Foo");
        Directory.CreateDirectory(srcDir);
        var sourceFile = Path.Combine(srcDir, "OrderHandler.cs");
        File.WriteAllText(sourceFile, "public class OrderHandler {}");

        var testsDir = Path.Combine(_solutionRoot, "tests", "Foo.Tests");
        Directory.CreateDirectory(testsDir);
        File.WriteAllText(Path.Combine(testsDir, "Foo.Tests.csproj"), "<Project></Project>");
        File.WriteAllText(Path.Combine(testsDir, "OrderHandlerTests.cs"), "existing content");

        var gap = new CoverageGap(
            ProjectPath: Path.Combine(srcDir, "Foo.csproj"),
            FilePath: sourceFile,
            Namespace: "Foo",
            TypeName: "OrderHandler",
            MemberName: "Handle",
            UncoveredLines: new[] { 1 });

        var result = await MakeGenerator().GenerateAsync(gap, _solutionRoot, CancellationToken.None);

        result.FilePath.ShouldBe(Path.Combine(testsDir, "OrderHandlerTests2.cs"));
    }

    [Fact]
    public async Task RegenerateAsync_KeepsPreviousFilePathAndUpdatesContent()
    {
        var previous = new GeneratedTest("/repo/tests/Foo.Tests/OrderHandlerTests.cs", "old content");
        var gap = new CoverageGap("proj.csproj", "src/OrderHandler.cs", "Foo", "OrderHandler", "Handle", new[] { 1 });

        var result = await MakeGenerator().RegenerateAsync(gap, previous, "CS0103 error", CancellationToken.None);

        result.FilePath.ShouldBe(previous.FilePath);
        result.Content.ShouldBe("public class GeneratedTests {}");
    }
}
