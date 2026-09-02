using CoverageCompletion.Contracts;
using CoverageCompletion.Generation;
using FluentAssertions;

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

        result.Should().Be(expected);
    }

    [Fact]
    public void FindExampleTest_NoNamingMatch_FallsBackToHandlerResultHeuristic()
    {
        var testDir = CreateTestProject("Foo.Tests");
        File.WriteAllText(Path.Combine(testDir, "Unrelated.cs"), "public class Unrelated { }");
        var fallbackContent =
            "public class SomeOtherHandlerTests { void Test() { Result<int> result = handler.Handle(); result.IsSuccess.Should().BeTrue(); } }";
        File.WriteAllText(Path.Combine(testDir, "SomeOtherHandlerTests.cs"), fallbackContent);

        var result = new TestPatternFinder().FindExampleTest(MakeGap(), _solutionRoot);

        result.Should().Be(fallbackContent);
    }

    [Fact]
    public void FindExampleTest_NoTestProjects_ReturnsNull()
    {
        var result = new TestPatternFinder().FindExampleTest(MakeGap(), _solutionRoot);

        result.Should().BeNull();
    }

    [Fact]
    public void FindExampleTest_TestProjectWithoutHandlerOrResultPattern_ReturnsNull()
    {
        var testDir = CreateTestProject("Foo.Tests");
        File.WriteAllText(Path.Combine(testDir, "PlainTests.cs"), "public class PlainTests { void Test() { Assert.True(true); } }");

        var result = new TestPatternFinder().FindExampleTest(MakeGap(), _solutionRoot);

        result.Should().BeNull();
    }

    [Fact]
    public void FindTestProjectDirectories_FindsByDirectorySuffixAndPackageReference()
    {
        var byName = CreateTestProject("Foo.UnitTests");
        var srcDir = Path.Combine(_solutionRoot, "Foo.NotATestDir");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Foo.csproj"), "<Project></Project>");

        var directories = new TestPatternFinder().FindTestProjectDirectories(_solutionRoot);

        directories.Should().Contain(byName);
        directories.Should().NotContain(srcDir);
    }
}
