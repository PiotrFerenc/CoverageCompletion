using System.Diagnostics;
using CoverageCompletion.Cli;
using CoverageCompletion.Contracts;
using CoverageCompletion.Infrastructure.Build;
using CoverageCompletion.Infrastructure.Coverage;
using CoverageCompletion.Infrastructure.Git;
using CoverageCompletion.Infrastructure.Reporting;
using Shouldly;
using Xunit;

namespace CoverageCompletion.EndToEnd.Tests;

/// <summary>
/// Full-stack smoke test: builds a real throwaway git repo + .NET solution on disk, runs
/// <see cref="CoverageCompletionRunner"/> against it with real git/dotnet subprocesses for
/// every collaborator except test generation (which is faked, since it would otherwise call
/// the real OpenAI API), and asserts on the actual on-disk/on-git side effects. Slow (real
/// `dotnet test --collect` + `dotnet build` + `dotnet test --filter` cycles) - the only test
/// in the "EndToEnd" category, deliberately.
/// </summary>
public sealed class CoverageCompletionEndToEndTests : IAsyncLifetime
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"cc-e2e-{Guid.NewGuid():N}");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        DeleteDirectoryRobust(_repoPath);
        return Task.CompletedTask;
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    public async Task RunAsync_CompletesUncoveredGap_CommitsIt_AndCleansUpTheWorktree()
    {
        // Arrange: a real git repo + real .NET solution with one intentionally uncovered method.
        CreateFixtureRepo(_repoPath);
        var solutionPath = Path.Combine(_repoPath, "Sample.sln");

        var worktreeManager = new RecordingWorktreeManager(new WorktreeManager());
        var runner = new CoverageCompletionRunner(
            worktreeManager,
            new CoverageAnalyzer(),
            new BuildTestRunner(),
            new GitCommitter(),
            new SummaryReporter(),
            new FakeTestGenerator());

        // Act
        var exitCode = await runner.RunAsync(_repoPath, solutionPath, CancellationToken.None);

        // Assert
        exitCode.ShouldBe(0, "the run should complete without cancellation or an unhandled failure");

        worktreeManager.LastWorktreePath.ShouldNotBeNull("the runner must have created a worktree");
        Directory.Exists(worktreeManager.LastWorktreePath).ShouldBeFalse(
            "the runner removes the worktree directory in its finally block");

        var branchListing = RunGit(_repoPath, "branch", "--list", "coverage/session-*");
        branchListing.ShouldNotBeNullOrWhiteSpace("a coverage/session-* branch should have been created");
        var branchName = branchListing.Trim().TrimStart('*', ' ');

        var branchLog = RunGit(_repoPath, "log", branchName, "--oneline");
        var commitCount = branchLog.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        commitCount.ShouldBeGreaterThan(1, "the branch should carry the generated-test commit on top of the initial commit");

        var worktreeListing = RunGit(_repoPath, "worktree", "list");
        worktreeListing.ShouldNotContain("coverage-worktrees", customMessage: "`git worktree remove` should have dropped the entry");

        var summaryFiles = Directory.GetFiles(_repoPath, "coverage-completion-summary-*.md");
        summaryFiles.ShouldHaveSingleItem("the runner always writes exactly one summary file per session");
        var summaryContent = await File.ReadAllTextAsync(summaryFiles[0]);
        summaryContent.ShouldContain("Calculator.Classify", customMessage: "the summary should mention the gap that got completed");
    }

    /// <summary>
    /// Two library projects (Sample.Lib.Alpha, Sample.Lib.Beta) sharing one test project, each
    /// with a single uncovered method. Guards against a real risk in a multi-project solution:
    /// <see cref="CoverageAnalyzer"/> resolving every <see cref="CoverageGap"/> back to the
    /// SAME project, or to the wrong one, instead of the csproj the gap's source file actually
    /// lives under.
    /// </summary>
    [Fact]
    [Trait("Category", "EndToEnd")]
    public async Task RunAsync_WithTwoLibraryProjects_AssignsEachGapToItsOwnProject_AndCommitsBoth()
    {
        // Arrange
        CreateTwoProjectFixtureRepo(_repoPath);
        var solutionPath = Path.Combine(_repoPath, "Sample.sln");

        var worktreeManager = new RecordingWorktreeManager(new WorktreeManager());
        var testGenerator = new TwoProjectFakeTestGenerator();
        var runner = new CoverageCompletionRunner(
            worktreeManager,
            new CoverageAnalyzer(),
            new BuildTestRunner(),
            new GitCommitter(),
            new SummaryReporter(),
            testGenerator);

        // Act
        var exitCode = await runner.RunAsync(_repoPath, solutionPath, CancellationToken.None);

        // Assert
        exitCode.ShouldBe(0, "the run should complete without cancellation or an unhandled failure");

        testGenerator.GeneratedGaps.Count.ShouldBe(2, "both Widget.Classify and Gadget.Sign should have been detected as gaps");

        var widgetGap = testGenerator.GeneratedGaps.Single(g => g.TypeName == "Widget");
        Path.GetFileName(widgetGap.ProjectPath).ShouldBe("Sample.Lib.Alpha.csproj",
            "the Widget gap's source file lives under Sample.Lib.Alpha, not Sample.Lib.Beta or the test project");
        Path.GetFileName(Path.GetDirectoryName(widgetGap.FilePath)).ShouldBe("Sample.Lib.Alpha");

        var gadgetGap = testGenerator.GeneratedGaps.Single(g => g.TypeName == "Gadget");
        Path.GetFileName(gadgetGap.ProjectPath).ShouldBe("Sample.Lib.Beta.csproj",
            "the Gadget gap's source file lives under Sample.Lib.Beta, not Sample.Lib.Alpha or the test project");
        Path.GetFileName(Path.GetDirectoryName(gadgetGap.FilePath)).ShouldBe("Sample.Lib.Beta");

        worktreeManager.LastWorktreePath.ShouldNotBeNull("the runner must have created a worktree");
        Directory.Exists(worktreeManager.LastWorktreePath).ShouldBeFalse(
            "the runner removes the worktree directory in its finally block");

        var branchListing = RunGit(_repoPath, "branch", "--list", "coverage/session-*");
        var branchName = branchListing.Trim().TrimStart('*', ' ');

        var branchLog = RunGit(_repoPath, "log", branchName, "--oneline");
        var commitCount = branchLog.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        commitCount.ShouldBe(3, "the initial commit plus one generated-test commit per gap");

        var worktreeListing = RunGit(_repoPath, "worktree", "list");
        worktreeListing.ShouldNotContain("coverage-worktrees", customMessage: "`git worktree remove` should have dropped the entry");

        var summaryFiles = Directory.GetFiles(_repoPath, "coverage-completion-summary-*.md");
        summaryFiles.ShouldHaveSingleItem("the runner always writes exactly one summary file per session");
        var summaryContent = await File.ReadAllTextAsync(summaryFiles[0]);
        summaryContent.ShouldContain("Widget.Classify");
        summaryContent.ShouldContain("Gadget.Sign");
    }

    // ---- fixture construction -------------------------------------------------------------

    private static void CreateFixtureRepo(string repoPath)
    {
        var libDir = Path.Combine(repoPath, "Sample.Lib");
        var testsDir = Path.Combine(repoPath, "Sample.Lib.Tests");
        Directory.CreateDirectory(libDir);
        Directory.CreateDirectory(testsDir);

        File.WriteAllText(Path.Combine(libDir, "Sample.Lib.csproj"), SampleLibCsproj);
        File.WriteAllText(Path.Combine(libDir, "Calculator.cs"), CalculatorSource);
        File.WriteAllText(Path.Combine(testsDir, "Sample.Lib.Tests.csproj"), SampleLibTestsCsproj);
        File.WriteAllText(Path.Combine(testsDir, "CalculatorTests.cs"), CalculatorTestsSource);
        File.WriteAllText(Path.Combine(repoPath, "Sample.sln"), SampleSln);

        RunGit(repoPath, "init", "-q");
        RunGit(repoPath, "config", "user.email", "e2e@example.com");
        RunGit(repoPath, "config", "user.name", "E2E Test");
        RunGit(repoPath, "add", "-A");
        RunGit(repoPath, "commit", "-q", "-m", "initial commit");
    }

    private const string SampleLibCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    // Classify() is the deliberate coverage gap: no test exercises it, but Add() does, so the
    // fixture also demonstrates the "existing passing test for a sibling method" style pattern.
    private const string CalculatorSource = """
        namespace Sample.Lib;

        public class Calculator
        {
            public int Add(int a, int b) => a + b;

            public int Classify(int n)
            {
                if (n > 0) return 1;
                if (n < 0) return -1;
                return 0;
            }
        }
        """;

    private const string SampleLibTestsCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <IsPackable>false</IsPackable>
            <RollForward>Major</RollForward>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.6.0" />
            <PackageReference Include="xunit" Version="2.4.2" />
            <PackageReference Include="xunit.runner.visualstudio" Version="2.4.5">
              <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
              <PrivateAssets>all</PrivateAssets>
            </PackageReference>
            <PackageReference Include="coverlet.collector" Version="6.0.0">
              <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
              <PrivateAssets>all</PrivateAssets>
            </PackageReference>
          </ItemGroup>
          <ItemGroup>
            <ProjectReference Include="..\Sample.Lib\Sample.Lib.csproj" />
          </ItemGroup>
        </Project>
        """;

    private const string CalculatorTestsSource = """
        using Xunit;
        using Sample.Lib;

        namespace Sample.Lib.Tests;

        public class CalculatorTests
        {
            [Fact]
            public void Add_ReturnsSum()
            {
                var calculator = new Calculator();
                Assert.Equal(5, calculator.Add(2, 3));
            }
        }
        """;

    private const string SampleSln = """
        Microsoft Visual Studio Solution File, Format Version 12.00
        # Visual Studio Version 17
        VisualStudioVersion = 17.0.31903.59
        MinimumVisualStudioVersion = 10.0.40219.1
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Sample.Lib", "Sample.Lib\Sample.Lib.csproj", "{A1111111-1111-1111-1111-111111111111}"
        EndProject
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Sample.Lib.Tests", "Sample.Lib.Tests\Sample.Lib.Tests.csproj", "{B2222222-2222-2222-2222-222222222222}"
        EndProject
        Global
        	GlobalSection(SolutionConfigurationPlatforms) = preSolution
        		Debug|Any CPU = Debug|Any CPU
        		Release|Any CPU = Release|Any CPU
        	EndGlobalSection
        	GlobalSection(ProjectConfigurationPlatforms) = postSolution
        		{A1111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        		{A1111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
        		{A1111111-1111-1111-1111-111111111111}.Release|Any CPU.ActiveCfg = Release|Any CPU
        		{A1111111-1111-1111-1111-111111111111}.Release|Any CPU.Build.0 = Release|Any CPU
        		{B2222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        		{B2222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
        		{B2222222-2222-2222-2222-222222222222}.Release|Any CPU.ActiveCfg = Release|Any CPU
        		{B2222222-2222-2222-2222-222222222222}.Release|Any CPU.Build.0 = Release|Any CPU
        	EndGlobalSection
        EndGlobal
        """;

    private static void CreateTwoProjectFixtureRepo(string repoPath)
    {
        var alphaDir = Path.Combine(repoPath, "Sample.Lib.Alpha");
        var betaDir = Path.Combine(repoPath, "Sample.Lib.Beta");
        var testsDir = Path.Combine(repoPath, "Sample.Tests");
        Directory.CreateDirectory(alphaDir);
        Directory.CreateDirectory(betaDir);
        Directory.CreateDirectory(testsDir);

        File.WriteAllText(Path.Combine(alphaDir, "Sample.Lib.Alpha.csproj"), SampleLibCsprojNoRefs);
        File.WriteAllText(Path.Combine(alphaDir, "Widget.cs"), WidgetSource);
        File.WriteAllText(Path.Combine(betaDir, "Sample.Lib.Beta.csproj"), SampleLibCsprojNoRefs);
        File.WriteAllText(Path.Combine(betaDir, "Gadget.cs"), GadgetSource);
        File.WriteAllText(Path.Combine(testsDir, "Sample.Tests.csproj"), SampleTestsCsproj);
        File.WriteAllText(Path.Combine(testsDir, "WidgetTests.cs"), WidgetTestsSource);
        File.WriteAllText(Path.Combine(testsDir, "GadgetTests.cs"), GadgetTestsSource);
        File.WriteAllText(Path.Combine(repoPath, "Sample.sln"), TwoProjectSampleSln);

        RunGit(repoPath, "init", "-q");
        RunGit(repoPath, "config", "user.email", "e2e@example.com");
        RunGit(repoPath, "config", "user.name", "E2E Test");
        RunGit(repoPath, "add", "-A");
        RunGit(repoPath, "commit", "-q", "-m", "initial commit");
    }

    private const string SampleLibCsprojNoRefs = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    // Classify() is the deliberate gap in Alpha: Double() is covered by WidgetTests, Classify() is not.
    private const string WidgetSource = """
        namespace Sample.Lib.Alpha;

        public class Widget
        {
            public int Double(int a) => a * 2;

            public int Classify(int n)
            {
                if (n > 0) return 1;
                if (n < 0) return -1;
                return 0;
            }
        }
        """;

    // Sign() is the deliberate gap in Beta: Square() is covered by GadgetTests, Sign() is not.
    private const string GadgetSource = """
        namespace Sample.Lib.Beta;

        public class Gadget
        {
            public int Square(int a) => a * a;

            public int Sign(int n)
            {
                if (n > 0) return 1;
                if (n < 0) return -1;
                return 0;
            }
        }
        """;

    private const string SampleTestsCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <IsPackable>false</IsPackable>
            <RollForward>Major</RollForward>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.6.0" />
            <PackageReference Include="xunit" Version="2.4.2" />
            <PackageReference Include="xunit.runner.visualstudio" Version="2.4.5">
              <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
              <PrivateAssets>all</PrivateAssets>
            </PackageReference>
            <PackageReference Include="coverlet.collector" Version="6.0.0">
              <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
              <PrivateAssets>all</PrivateAssets>
            </PackageReference>
          </ItemGroup>
          <ItemGroup>
            <ProjectReference Include="..\Sample.Lib.Alpha\Sample.Lib.Alpha.csproj" />
            <ProjectReference Include="..\Sample.Lib.Beta\Sample.Lib.Beta.csproj" />
          </ItemGroup>
        </Project>
        """;

    private const string WidgetTestsSource = """
        using Xunit;
        using Sample.Lib.Alpha;

        namespace Sample.Tests;

        public class WidgetTests
        {
            [Fact]
            public void Double_ReturnsDoubledValue()
            {
                var widget = new Widget();
                Assert.Equal(10, widget.Double(5));
            }
        }
        """;

    private const string GadgetTestsSource = """
        using Xunit;
        using Sample.Lib.Beta;

        namespace Sample.Tests;

        public class GadgetTests
        {
            [Fact]
            public void Square_ReturnsSquaredValue()
            {
                var gadget = new Gadget();
                Assert.Equal(25, gadget.Square(5));
            }
        }
        """;

    private const string TwoProjectSampleSln = """
        Microsoft Visual Studio Solution File, Format Version 12.00
        # Visual Studio Version 17
        VisualStudioVersion = 17.0.31903.59
        MinimumVisualStudioVersion = 10.0.40219.1
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Sample.Lib.Alpha", "Sample.Lib.Alpha\Sample.Lib.Alpha.csproj", "{C3333333-3333-3333-3333-333333333333}"
        EndProject
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Sample.Lib.Beta", "Sample.Lib.Beta\Sample.Lib.Beta.csproj", "{D4444444-4444-4444-4444-444444444444}"
        EndProject
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Sample.Tests", "Sample.Tests\Sample.Tests.csproj", "{E5555555-5555-5555-5555-555555555555}"
        EndProject
        Global
        	GlobalSection(SolutionConfigurationPlatforms) = preSolution
        		Debug|Any CPU = Debug|Any CPU
        		Release|Any CPU = Release|Any CPU
        	EndGlobalSection
        	GlobalSection(ProjectConfigurationPlatforms) = postSolution
        		{C3333333-3333-3333-3333-333333333333}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        		{C3333333-3333-3333-3333-333333333333}.Debug|Any CPU.Build.0 = Debug|Any CPU
        		{C3333333-3333-3333-3333-333333333333}.Release|Any CPU.ActiveCfg = Release|Any CPU
        		{C3333333-3333-3333-3333-333333333333}.Release|Any CPU.Build.0 = Release|Any CPU
        		{D4444444-4444-4444-4444-444444444444}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        		{D4444444-4444-4444-4444-444444444444}.Debug|Any CPU.Build.0 = Debug|Any CPU
        		{D4444444-4444-4444-4444-444444444444}.Release|Any CPU.ActiveCfg = Release|Any CPU
        		{D4444444-4444-4444-4444-444444444444}.Release|Any CPU.Build.0 = Release|Any CPU
        		{E5555555-5555-5555-5555-555555555555}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        		{E5555555-5555-5555-5555-555555555555}.Debug|Any CPU.Build.0 = Debug|Any CPU
        		{E5555555-5555-5555-5555-555555555555}.Release|Any CPU.ActiveCfg = Release|Any CPU
        		{E5555555-5555-5555-5555-555555555555}.Release|Any CPU.Build.0 = Release|Any CPU
        	EndGlobalSection
        EndGlobal
        """;

    // ---- process / filesystem helpers ------------------------------------------------------

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {stderr}");
        }

        return stdout;
    }

    private static void DeleteDirectoryRobust(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of a temp directory; never fail the test over leftovers.
        }
    }

    /// <summary>
    /// Wraps the real <see cref="WorktreeManager"/> just to capture the path it hands back, so
    /// the test can assert the directory is gone after the run without reimplementing worktree
    /// creation.
    /// </summary>
    private sealed class RecordingWorktreeManager(IWorktreeManager inner) : IWorktreeManager
    {
        public string? LastWorktreePath { get; private set; }

        public async Task<WorktreeSession> CreateAsync(string repoPath, CancellationToken ct)
        {
            var session = await inner.CreateAsync(repoPath, ct);
            LastWorktreePath = session.WorktreePath;
            return session;
        }

        public Task RemoveAsync(WorktreeSession session, CancellationToken ct) => inner.RemoveAsync(session, ct);
    }

    /// <summary>
    /// Stands in for the real OpenAI-backed <c>TestGenerator</c>. Knows about exactly one gap
    /// shape - <c>Calculator.Classify</c> from the fixture solution above - and always emits the
    /// same compiling, passing xUnit test for it.
    /// </summary>
    private sealed class FakeTestGenerator : ITestGenerator
    {
        public Task<GeneratedTest> GenerateAsync(CoverageGap gap, string solutionPath, CancellationToken ct)
        {
            var solutionDir = Path.GetDirectoryName(solutionPath)!;
            var testProjectDir = Path.Combine(solutionDir, "Sample.Lib.Tests");
            var filePath = Path.Combine(testProjectDir, $"{gap.TypeName}GeneratedTests.cs");
            return Task.FromResult(Build(gap, filePath));
        }

        public Task<GeneratedTest> RegenerateAsync(CoverageGap gap, GeneratedTest previous, string buildError, CancellationToken ct)
            => Task.FromResult(Build(gap, previous.FilePath));

        private static GeneratedTest Build(CoverageGap gap, string filePath)
        {
            if (gap.TypeName != "Calculator" || !gap.MemberName.StartsWith("Classify", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"FakeTestGenerator only knows how to cover Calculator.Classify, got {gap.TypeName}.{gap.MemberName}.");
            }

            var content = """
                using Xunit;
                using Sample.Lib;

                namespace Sample.Lib.Tests;

                public class CalculatorGeneratedTests
                {
                    [Theory]
                    [InlineData(5, 1)]
                    [InlineData(-5, -1)]
                    [InlineData(0, 0)]
                    public void Classify_ReturnsExpectedSign(int input, int expected)
                    {
                        var calculator = new Calculator();
                        Assert.Equal(expected, calculator.Classify(input));
                    }
                }
                """;

            return new GeneratedTest(filePath, content);
        }
    }

    /// <summary>
    /// Stands in for the real OpenAI-backed <c>TestGenerator</c> for the two-project fixture.
    /// Records every <see cref="CoverageGap"/> it was asked to cover (so the test can assert on
    /// <see cref="CoverageGap.ProjectPath"/>/<see cref="CoverageGap.FilePath"/> after the run) and
    /// always emits the same compiling, passing xUnit test for each of the two known gap shapes.
    /// </summary>
    private sealed class TwoProjectFakeTestGenerator : ITestGenerator
    {
        public List<CoverageGap> GeneratedGaps { get; } = [];

        public Task<GeneratedTest> GenerateAsync(CoverageGap gap, string solutionPath, CancellationToken ct)
        {
            GeneratedGaps.Add(gap);

            var solutionDir = Path.GetDirectoryName(solutionPath)!;
            var testProjectDir = Path.Combine(solutionDir, "Sample.Tests");
            var filePath = Path.Combine(testProjectDir, $"{gap.TypeName}GeneratedTests.cs");
            return Task.FromResult(Build(gap, filePath));
        }

        public Task<GeneratedTest> RegenerateAsync(CoverageGap gap, GeneratedTest previous, string buildError, CancellationToken ct)
            => Task.FromResult(Build(gap, previous.FilePath));

        private static GeneratedTest Build(CoverageGap gap, string filePath)
        {
            var content = (gap.TypeName, gap.MemberName) switch
            {
                ("Widget", "Classify") => """
                    using Xunit;
                    using Sample.Lib.Alpha;

                    namespace Sample.Tests;

                    public class WidgetGeneratedTests
                    {
                        [Theory]
                        [InlineData(5, 1)]
                        [InlineData(-5, -1)]
                        [InlineData(0, 0)]
                        public void Classify_ReturnsExpectedSign(int input, int expected)
                        {
                            var widget = new Widget();
                            Assert.Equal(expected, widget.Classify(input));
                        }
                    }
                    """,
                ("Gadget", "Sign") => """
                    using Xunit;
                    using Sample.Lib.Beta;

                    namespace Sample.Tests;

                    public class GadgetGeneratedTests
                    {
                        [Theory]
                        [InlineData(5, 1)]
                        [InlineData(-5, -1)]
                        [InlineData(0, 0)]
                        public void Sign_ReturnsExpectedSign(int input, int expected)
                        {
                            var gadget = new Gadget();
                            Assert.Equal(expected, gadget.Sign(input));
                        }
                    }
                    """,
                _ => throw new InvalidOperationException(
                    $"TwoProjectFakeTestGenerator only knows how to cover Widget.Classify and Gadget.Sign, got {gap.TypeName}.{gap.MemberName}."),
            };

            return new GeneratedTest(filePath, content);
        }
    }
}
