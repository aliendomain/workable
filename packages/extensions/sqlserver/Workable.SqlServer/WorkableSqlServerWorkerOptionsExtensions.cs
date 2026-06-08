using System.Data.Common;

namespace Workable.SqlServer;

public static class WorkableSqlServerWorkerOptionsExtensions
{
    public static Task CompleteDurablyWithSqlServerTransaction(
        this IWorkExecutionContext context,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        return context.CompleteDurably(
            new WorkableSqlServerQueueDurabilityTransaction(connection, transaction),
            cancellationToken);
    }

    public static WorkerOptions WithSqlServerQueueDurabilityTransaction(
        this WorkerOptions options,
        DbConnection connection,
        DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        return options with
        {
            QueueDurabilityTransaction = new WorkableSqlServerQueueDurabilityTransaction(connection, transaction),
        };
    }
}
