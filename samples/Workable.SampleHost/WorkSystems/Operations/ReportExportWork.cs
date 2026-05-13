using Workable;

namespace Workable.SampleHost.Operations;

public sealed record ReportExportInput(
    ReportFormat Format,
    DateRange Range,
    IReadOnlyList<string> Metrics,
    bool IncludeCharts,
    string? TimeZone = "UTC");

public sealed record DateRange(DateOnly Start, DateOnly End);

public sealed record ReportExportOutput(
    string ExportId,
    ReportFormat Format,
    int RowCount,
    string DownloadPath);

public enum ReportFormat
{
    Csv,
    Xlsx,
    Pdf,
}

[WorkMetadata("analytics.report.export", "Analytics:Reports", "Exports a metrics report for a requested date range.")]
public sealed class ReportExportWork : IWorkExecutor<ReportExportInput, ReportExportOutput>
{
    public Task<WorkExecutionResult<ReportExportOutput>> Execute(
        IWorkExecutionContext context,
        ReportExportInput input,
        CancellationToken cancellationToken)
    {
        var days = Math.Max(1, input.Range.End.DayNumber - input.Range.Start.DayNumber + 1);
        var rows = days * Math.Max(1, input.Metrics.Count);

        return Task.FromResult(WorkExecutionResult<ReportExportOutput>.Success(new ReportExportOutput(
            $"export_{Guid.NewGuid():N}"[..18],
            input.Format,
            rows,
            $"/exports/{DateTimeOffset.UtcNow:yyyy/MM/dd}/report.{input.Format.ToString().ToLowerInvariant()}")));
    }
}
