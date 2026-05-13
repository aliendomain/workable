using Workable;

namespace Workable.SampleHost.Operations;

public sealed record DataImportInput(
    Uri SourceUri,
    ImportMode Mode,
    string TargetTable,
    bool HasHeaderRow = true,
    IReadOnlyDictionary<string, string>? ColumnMap = null);

public sealed record DataImportOutput(
    string ImportId,
    ImportMode Mode,
    int AcceptedRows,
    int RejectedRows);

public enum ImportMode
{
    Append,
    Replace,
    Upsert,
}

[WorkMetadata("data.import.csv", "Data:Imports", "Imports tabular data from a source URI.")]
public sealed class DataImportWork : IWorkExecutor<DataImportInput, DataImportOutput>
{
    public Task<WorkExecutionResult<DataImportOutput>> Execute(
        IWorkExecutionContext context,
        DataImportInput input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult<DataImportOutput>.Success(new DataImportOutput(
            $"import_{Guid.NewGuid():N}"[..19],
            input.Mode,
            Random.Shared.Next(50, 500),
            Random.Shared.Next(0, 5))));
}
