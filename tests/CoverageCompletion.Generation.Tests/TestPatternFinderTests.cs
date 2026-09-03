using CoverageCompletion.Contracts;
using CoverageCompletion.Generation;
using Shouldly;

namespace CoverageCompletion.Generation.Tests;

public class TestPatternFinderTests : IDisposable
{
    private readonly string _solutionRoot;

    public TestPatternFinderTests()
    {
        _solutionRoot = Path.Combine(Path.GetTempPath(), "CoverageCompletionTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_solutionRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_solutionRoot))
        {
            Directory.Delete(_solutionRoot, recursive: true);
        }
    }

    private static CoverageGap MakeGap(string typeName = "OrderHandler") => new(
        ProjectPath: "src/Foo/Foo.csproj",
        FilePath: "src/Foo/OrderHandler.cs",
        Namespace: "Foo",
        TypeName: typeName,
        MemberName: "Handle",
        UncoveredLines: new[] { 1 });

    private string CreateTestProject(string dirName)
    {
        var dir = Path.Combine(_solutionRoot, dirName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, dirName + ".csproj"),
            "<Project><ItemGroup><PackageReference Include=\"xunit\" Version=\"2.4.2\" /></ItemGroup></Project>");
        return dir;
    }

    [Fact]
    public void FindExampleTest_ByNamingConvention_ReturnsFileContent()
    {
        var testDir = CreateTestProject("Foo.Tests");
        var expected = "public class OrderHandlerTests { }";
        File.WriteAllText(Path.Combine(testDir, "OrderHandlerTests.cs"), expected);

        var result = new TestPatternFinder().FindExampleTest(MakeGap(), _solutionRoot);

        result.ShouldBe(expected);
    }

    [Fact]
    public void FindExampleTest_NoNamingMatch_FallsBackToHandlerResultHeuristic()
    {
        var testDir = CreateTestProject("Foo.Tests");
        File.WriteAllText(Path.Combine(testDir, "Unrelated.cs"), "public class Unrelated { }");
        var fallbackContent = """
            public class SomeOtherTests
            {
                [Fact]
                public void Handle_ReturnsSuccess()
                {
                    var sut = new OrderHandler();
                    Result<int> result = sut.Handle();
                    result.IsSuccess.Should().BeTrue();
                }
            }
            """;
        File.WriteAllText(Path.Combine(testDir, "SomeOtherTests.cs"), fallbackContent);

        var result = new TestPatternFinder().FindExampleTest(MakeGap(), _solutionRoot);

        result.ShouldBe(fallbackContent);
    }

    [Fact]
    public void FindExampleTest_HandlerWordAloneWithoutFactOrResultAssertion_DoesNotMatch()
    {
        var testDir = CreateTestProject("Foo.Tests");
        // Mentions "Handler" and even an IRequestHandler-shaped identifier, but has neither
        // a [Fact]/[Theory] nor a Result-style assertion - should not be picked as a fallback.
        var content = """
            public class HandlerUtility
            {
                // The Handler processes things.
                public void DoSomething(IRequestHandler handler)
                {
                    handler.Process();
                }
            }
            """;
        File.WriteAllText(Path.Combine(testDir, "HandlerUtility.cs"), content);

        var result = new TestPatternFinder().FindExampleTest(MakeGap(), _solutionRoot);

        result.ShouldBeNull();
    }

    [Fact]
    public void FindExampleTest_MissingResultAssertion_DoesNotMatch()
    {
        var testDir = CreateTestProject("Foo.Tests");
        var content = """
            public class SomeOtherTests
            {
                [Fact]
                public void Handle_DoesSomething()
                {
                    var sut = new OrderHandler();
                    sut.Handle();
                }
            }
            """;
        File.WriteAllText(Path.Combine(testDir, "SomeOtherTests.cs"), content);

        var result = new TestPatternFinder().FindExampleTest(MakeGap(), _solutionRoot);

        result.ShouldBeNull();
    }

    [Fact]
    public void FindExampleTest_HandlerSubstringEmbeddedInLargerIdentifier_DoesNotSatisfyHandlerSignal()
    {
        var testDir = CreateTestProject("Foo.Tests");
        // "HandlerTests" and "Handlers" contain the substring "Handler" but are not identifiers
        // ending in "Handler" - the old Contains("Handler") check would wrongly match these.
        var content = """
            public class SomeHandlerTests
            {
                [Fact]
                public void Test()
                {
                    var handlers = GetHandlers();
                    Result<int> result = handlers.First().Handle();
                    result.IsSuccess.Should().BeTrue();
                }
            }
            """;
        File.WriteAllText(Path.Combine(testDir, "SomeHandlerTests.cs"), content);

        var result = new TestPatternFinder().FindExampleTest(MakeGap(), _solutionRoot);

        result.ShouldBeNull();
    }

    [Fact]
    public void FindExampleTest_NoTestProjects_ReturnsNull()
    {
        var result = new TestPatternFinder().FindExampleTest(MakeGap(), _solutionRoot);

        result.ShouldBeNull();
    }

    [Fact]
    public void FindExampleTest_TestProjectWithoutHandlerOrResultPattern_ReturnsNull()
    {
        var testDir = CreateTestProject("Foo.Tests");
        File.WriteAllText(Path.Combine(testDir, "PlainTests.cs"), "public class PlainTests { void Test() { Assert.True(true); } }");

        var result = new TestPatternFinder().FindExampleTest(MakeGap(), _solutionRoot);

        result.ShouldBeNull();
    }

    [Fact]
    public void FindTestProjectDirectories_FindsByDirectorySuffixAndPackageReference()
    {
        var byName = CreateTestProject("Foo.UnitTests");
        var srcDir = Path.Combine(_solutionRoot, "Foo.NotATestDir");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Foo.csproj"), "<Project></Project>");

        var directories = new TestPatternFinder().FindTestProjectDirectories(_solutionRoot);

        directories.ShouldContain(byName);
        directories.ShouldNotContain(srcDir);
    }

    [Fact]
    public void FindTestProjectDirectories_AcceptsSlnFilePathNotJustDirectory()
    {
        // Regression test: CoverageCompletionRunner always calls this with the path to the .sln
        // FILE (never its containing directory). A real-key end-to-end run against a Mediator
        // fixture showed the original `Directory.Exists(solutionPath)` guard silently returned
        // an empty list for every real invocation - FindExampleTest never found a style pattern,
        // and TestGenerator.BuildTestFilePath fell back to writing generated tests into the
        // SOURCE project's own directory instead of the test project's, which then made
        // DotnetPackageEnsurer add FluentAssertions to the wrong .csproj and break the build for
        // the whole retry loop. Unit tests never caught this because they all passed a bare
        // directory, not a .sln file path.
        var byName = CreateTestProject("Foo.Tests");
        var slnPath = Path.Combine(_solutionRoot, "Solution.sln");
        File.WriteAllText(slnPath, "Microsoft Visual Studio Solution File, Format Version 12.00");

        var directories = new TestPatternFinder().FindTestProjectDirectories(slnPath);

        directories.ShouldContain(byName);
    }
}
