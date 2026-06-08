using System.Data.Common;

namespace Workable.SqlServer;

public sealed record WorkableSqlServerQueueDurabilityTransaction(
    DbConnection Connection,
    DbTransaction Transaction) : IWorkQueueDurabilityTransaction;
