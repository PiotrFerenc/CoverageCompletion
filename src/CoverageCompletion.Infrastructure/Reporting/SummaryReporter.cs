using System.Text;
using CoverageCompletion.Contracts;

namespace CoverageCompletion.Infrastructure.Reporting;

/// <summary>
/// Accumulates completed/skipped coverage gaps in memory and writes a Markdown summary on demand.
/// </summary>
public sealed class SummaryReporter : ISummaryReporter
{
    private readonly List<(CoverageGap Gap, string CommitSha)> _completed = [];
    private readonly List<(CoverageGap Gap, string Reason)> _skipped = [];

    public void RecordCompleted(CoverageGap gap, string commitSha) => _completed.Add((gap, commitSha));

    public void RecordSkipped(CoverageGap gap, string reason) => _skipped.Add((gap, reason));

    public async Task WriteAsync(string path, CancellationToken ct)
    {
        var report = new StringBuilder()
            .AppendLine("# Coverage Completion Summary")
            .AppendLine()
            .AppendLine($"- Completed: {_completed.Count}")
            .AppendLine($"- Skipped: {_skipped.Count}")
            .AppendLine();

        if (_completed.Count > 0)
        {
            report.AppendLine("## Completed").AppendLine();
            foreach (var (gap, commitSha) in _completed)
            {
                report.AppendLine($"- `{gap.TypeName}.{gap.MemberName}` -> {commitSha}");
            }

            report.AppendLine();
        }

        if (_skipped.Count > 0)
        {
            report.AppendLine("## Skipped").AppendLine();
            foreach (var (gap, reason) in _skipped)
            {
                report.AppendLine($"- `{gap.TypeName}.{gap.MemberName}`: {reason}");
            }
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(path, report.ToString(), ct);
    }
}
