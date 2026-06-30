using System.Data.Common;

namespace Workable.SqlServer;

internal sealed class WorkableSqlServerWorkflowPersistenceTransaction(
    DbConnection connection,
    DbTransaction transaction) : IWorkflowPersistenceTransaction
{
    private bool committed;

    public DbConnection Connection { get; } = connection;

    public DbTransaction Transaction { get; } = transaction;

    public async Task Commit(CancellationToken cancellationToken = default)
    {
        await this.Transaction.CommitAsync(cancellationToken);
        this.committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!this.committed && this.Transaction.Connection is not null)
            {
                await this.Transaction.RollbackAsync(CancellationToken.None);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            await this.Transaction.DisposeAsync();
            await this.Connection.DisposeAsync();
        }
    }
}
