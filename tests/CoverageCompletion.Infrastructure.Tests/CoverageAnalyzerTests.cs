using CoverageCompletion.Infrastructure.Coverage;
using Shouldly;

namespace CoverageCompletion.Infrastructure.Tests;

/// <summary>
/// Exercises CoverageAnalyzer against a real two-project solution (lib + test project, no
/// fakes) since the whole point of this class is shelling out to a real
/// `dotnet test --collect:"XPlat Code Coverage"` and turning the coverlet-produced Cobertura
/// report into CoverageGaps mapped back to the nearest .csproj.
/// </summary>
public sealed class CoverageAnalyzerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("coverage-analyzer-").FullName;
    private readonly CoverageAnalyzer _sut = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CreateSolution()
    {
        var libDir = Path.Combine(_root, "Lib");
        var testsDir = Path.Combine(_root, "Lib.Tests");
        Directory.CreateDirectory(libDir);
        Directory.CreateDirectory(testsDir);

        File.WriteAllText(Path.Combine(libDir, "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(libDir, "Calculator.cs"), """
            namespace Lib;

            public class Calculator
            {
                public int Add(int a, int b) => a + b;

                public int Subtract(int a, int b) => a - b;
            }
            """);

        var testsCsprojPath = Path.Combine(testsDir, "Lib.Tests.csproj");
        File.WriteAllText(testsCsprojPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.6.0" />
                <PackageReference Include="xunit" Version="2.4.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.4.5" />
                <PackageReference Include="coverlet.collector" Version="6.0.0">
                  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                  <PrivateAssets>all</PrivateAssets>
                </PackageReference>
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(testsDir, "CalculatorTests.cs"), """
            using Lib;
            using Xunit;

            public class CalculatorTests
            {
                // Only Add is exercised - Subtract is the coverage gap this test suite exists to find.
                [Fact]
                public void Add_ReturnsSum() => Assert.Equal(3, new Calculator().Add(1, 2));
            }
            """);

        return testsCsprojPath;
    }

    [Fact]
    public async Task AnalyzeAsync_UntestedMethod_ReturnsGapMappedToTheSourceProjectsCsproj()
    {
        var testsCsprojPath = CreateSolution();

        var gaps = await _sut.AnalyzeAsync(testsCsprojPath, CancellationToken.None);

        var gap = gaps.ShouldHaveSingleItem();
        gap.TypeName.ShouldBe("Calculator");
        gap.MemberName.ShouldBe("Subtract");
        gap.ProjectPath.ShouldBe(Path.Combine(_root, "Lib", "Lib.csproj"));
    }
}
