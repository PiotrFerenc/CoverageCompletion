using CoverageCompletion.Contracts;
using CoverageCompletion.Infrastructure.Reporting;
using Shouldly;

namespace CoverageCompletion.Infrastructure.Tests;

public class SummaryReporterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("summary-reporter-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CoverageGap Gap(string typeName, string memberName) =>
        new("Foo.csproj", "Foo.cs", "MyNamespace", typeName, memberName, [1]);

    [Fact]
    public async Task WriteAsync_NothingRecorded_WritesZeroCountsAndNoSections()
    {
        var sut = new SummaryReporter();
        var path = Path.Combine(_dir, "summary.md");

        await sut.WriteAsync(path, CancellationToken.None);

        var content = await File.ReadAllTextAsync(path);
        content.ShouldContain("- Completed: 0");
        content.ShouldContain("- Skipped: 0");
        content.ShouldNotContain("## Completed");
        content.ShouldNotContain("## Skipped");
    }

    [Fact]
    public async Task WriteAsync_RecordedCompletedAndSkipped_ListsBothWithDetails()
    {
        var sut = new SummaryReporter();
        sut.RecordCompleted(Gap("Widget", "DoWork"), "abc123");
        sut.RecordSkipped(Gap("Gadget", "Fail"), "build failed");
        var path = Path.Combine(_dir, "summary.md");

        await sut.WriteAsync(path, CancellationToken.None);

        var content = await File.ReadAllTextAsync(path);
        content.ShouldContain("- Completed: 1");
        content.ShouldContain("- Skipped: 1");
        content.ShouldContain("`Widget.DoWork` -> abc123");
        content.ShouldContain("`Gadget.Fail`: build failed");
    }

    [Fact]
    public async Task WriteAsync_TargetDirectoryDoesNotExistYet_CreatesIt()
    {
        var sut = new SummaryReporter();
        var path = Path.Combine(_dir, "nested", "deeper", "summary.md");

        await sut.WriteAsync(path, CancellationToken.None);

        File.Exists(path).ShouldBeTrue();
    }
}
