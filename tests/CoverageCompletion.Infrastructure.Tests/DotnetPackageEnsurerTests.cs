using CoverageCompletion.Infrastructure.Packages;
using Shouldly;

namespace CoverageCompletion.Infrastructure.Tests;

/// <summary>
/// Exercises DotnetPackageEnsurer against a real minimal SDK-style .csproj (no fakes for
/// dotnet/NuGet) since the whole point of this class is shelling out to the real
/// `dotnet add package` and reading back real file content. Requires network access for the
/// cases that actually add a package.
/// </summary>
public sealed class DotnetPackageEnsurerTests : IDisposable
{
    private const string MinimalCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private readonly string _projectDir = Directory.CreateTempSubdirectory("pkg-ensurer-").FullName;
    private readonly DotnetPackageEnsurer _sut = new();

    public void Dispose() => Directory.Delete(_projectDir, recursive: true);

    private string CreateCsproj(string content)
    {
        var path = Path.Combine(_projectDir, "Sample.csproj");
        File.WriteAllText(path, content);
        return path;
    }

    private string TestFilePath => Path.Combine(_projectDir, "SomeTests.cs");

    [Fact]
    public async Task EnsureRequiredPackagesAsync_BothPackagesMissing_AddsBoth()
    {
        var csprojPath = CreateCsproj(MinimalCsproj);

        await _sut.EnsureRequiredPackagesAsync(TestFilePath, CancellationToken.None);

        var content = await File.ReadAllTextAsync(csprojPath);
        content.ShouldContain("Include=\"Shouldly\"");
        content.ShouldContain("Include=\"NSubstitute\"");
    }

    [Fact]
    public async Task EnsureRequiredPackagesAsync_OnePackageAlreadyPresent_AddsOnlyTheMissingOne()
    {
        var csprojPath = CreateCsproj(MinimalCsproj.Replace(
            "<PropertyGroup>",
            "<ItemGroup><PackageReference Include=\"Shouldly\" Version=\"4.2.1\" /></ItemGroup>\n  <PropertyGroup>"));

        await _sut.EnsureRequiredPackagesAsync(TestFilePath, CancellationToken.None);

        var content = await File.ReadAllTextAsync(csprojPath);
        CountOccurrences(content, "Include=\"Shouldly\"").ShouldBe(1);
        content.ShouldContain("Include=\"NSubstitute\"");
    }

    private static int CountOccurrences(string content, string substring) =>
        content.Split(substring).Length - 1;

    [Fact]
    public async Task EnsureRequiredPackagesAsync_BothPackagesAlreadyPresent_DoesNothing()
    {
        var original = MinimalCsproj.Replace(
            "<PropertyGroup>",
            "<ItemGroup>\n    <PackageReference Include=\"Shouldly\" Version=\"4.2.1\" />\n"
                + "    <PackageReference Include=\"NSubstitute\" Version=\"5.1.0\" />\n  </ItemGroup>\n  <PropertyGroup>");
        var csprojPath = CreateCsproj(original);

        await _sut.EnsureRequiredPackagesAsync(TestFilePath, CancellationToken.None);

        var content = await File.ReadAllTextAsync(csprojPath);
        content.ShouldBe(original);
    }
}
