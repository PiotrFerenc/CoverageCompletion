using CoverageCompletion.Infrastructure.Build;
using Shouldly;

namespace CoverageCompletion.Infrastructure.Tests;

/// <summary>
/// Exercises BuildTestRunner against a real minimal project (no fakes) since the whole point
/// of this class is shelling out to real `dotnet build`/`dotnet test` and reporting their exit
/// code + combined output.
/// </summary>
public sealed class BuildTestRunnerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("build-runner-").FullName;
    private readonly BuildTestRunner _sut = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteLibProject(string classBody)
    {
        File.WriteAllText(Path.Combine(_dir, "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_dir, "Calculator.cs"), classBody);
    }

    [Fact]
    public async Task BuildAsync_ValidProject_ReturnsSuccessTrue()
    {
        WriteLibProject("public class Calculator { public int Add(int a, int b) => a + b; }");

        var result = await _sut.BuildAsync(_dir, CancellationToken.None);

        result.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task BuildAsync_ProjectWithCompilerError_ReturnsSuccessFalseWithErrorInOutput()
    {
        WriteLibProject("public class Calculator { this is not valid C# }");

        var result = await _sut.BuildAsync(_dir, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Output.ShouldContain("error");
    }

    [Fact]
    public async Task RunTestsAsync_FilterMatchesAPassingTest_ReturnsSuccessTrue()
    {
        File.WriteAllText(Path.Combine(_dir, "Lib.Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.6.0" />
                <PackageReference Include="xunit" Version="2.4.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.4.5" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_dir, "CalculatorTests.cs"), """
            using Xunit;
            public class CalculatorTests
            {
                [Fact]
                public void Add_ReturnsSum() => Assert.Equal(3, 1 + 2);
            }
            """);

        (await _sut.BuildAsync(_dir, CancellationToken.None)).Success.ShouldBeTrue();

        var result = await _sut.RunTestsAsync(_dir, "FullyQualifiedName~CalculatorTests", CancellationToken.None);

        result.Success.ShouldBeTrue();
    }
}
