using Microsoft.Data.SqlClient;
using Workable.SqlServer;

namespace Workable.SampleHost.Demo;

public sealed class DemoDurabilityWarningController(
    IWorkSystemRegistry registry,
    DemoSampleSystemSelection systemSelection,
    string connectionString,
    ILogger<DemoDurabilityWarningController> logger) : IAsyncDisposable
{
    private const int DefaultWorkerCount = 3;

    private readonly Lock sync = new();
    private SqlConnection? connection;
    private SqlTransaction? transaction;
    private CancellationTokenSource? cancellation;
    private Task[] waitingTasks = [];
    private DateTimeOffset? startedAt;
    private int queuedWorkerCount;
    private bool disposed;

    public DemoDurabilityWarningStatus Status()
    {
        lock (this.sync)
        {
            return new DemoDurabilityWarningStatus(
                IsRunning: this.transaction is not null,
                WorkerCount: this.queuedWorkerCount,
                StartedAt: this.startedAt,
                Message: this.transaction is null
                    ? "Ready. Start and leave it running for about 30 seconds to trigger durability waiter warnings."
                    : "Holding durable enqueues inside an uncommitted SQL transaction so accepted waiters stay pending.");
        }
    }

    public async Task<DemoDurabilityWarningStatus> Start(CancellationToken cancellationToken)
    {
        lock (this.sync)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (this.transaction is not null)
            {
                return this.Status();
            }
        }

        if (!systemSelection.Current.Operations)
        {
            return new DemoDurabilityWarningStatus(
                IsRunning: false,
                WorkerCount: 0,
                StartedAt: null,
                Message: "Operations is disabled.");
        }

        SqlConnection? openedConnection = null;
        SqlTransaction? openedTransaction = null;
        CancellationTokenSource? waitingCancellation = null;
        var waitingTasks = new List<Task>();
        var queuedWorkers = 0;

        try
        {
            openedConnection = new SqlConnection(connectionString);
            await openedConnection.OpenAsync(cancellationToken);
            openedTransaction = (SqlTransaction)await openedConnection.BeginTransactionAsync(cancellationToken);
            waitingCancellation = new CancellationTokenSource();

            for (var index = 0; index < DefaultWorkerCount; index++)
            {
                var subjectValue = $"{Guid.NewGuid():N}:{index}";
                var handle = await registry.Default.Queue.Enqueue(
                    "sample.demo.durable",
                    WorkInput.FromValue(
                        new DemoTimedInput(
                            $"durability warning sample #{index + 1}",
                            2_000,
                            DiscoveredIdentifierType: "durability-warning",
                            DiscoveredIdentifierValue: subjectValue),
                        subjectId: new WorkSubjectId("sample-durable-warning", subjectValue),
                        identifiers:
                        [
                            new WorkIdentifier("sample-workload", "durability-warning"),
                            new WorkIdentifier("durability-warning", subjectValue),
                        ]),
                    WorkerOptions.Default.WithSqlServerQueueDurabilityTransaction(openedConnection, openedTransaction),
                    cancellationToken);

                if (!handle.QueueOutcome.IsAccepted)
                {
                    throw new InvalidOperationException(
                        $"Expected durable warning sample enqueue to be accepted, but received {handle.QueueOutcome.Status}.");
                }

                queuedWorkers++;
                waitingTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await handle.WaitForCompletion(waitingCancellation.Token);
                    }
                    catch (OperationCanceledException) when (waitingCancellation.IsCancellationRequested)
                    {
                        logger.LogDebug("Durability warning sample waiter canceled during intentional cleanup.");
                    }
                }, CancellationToken.None));
            }

            lock (this.sync)
            {
                if (this.disposed)
                {
                    throw new ObjectDisposedException(nameof(DemoDurabilityWarningController));
                }

                this.connection = openedConnection;
                this.transaction = openedTransaction;
                this.cancellation = waitingCancellation;
                this.waitingTasks = [.. waitingTasks];
                this.queuedWorkerCount = queuedWorkers;
                this.startedAt = DateTimeOffset.UtcNow;
            }

            return this.Status();
        }
        catch
        {
            waitingCancellation?.Cancel();
            try
            {
                await Task.WhenAll(waitingTasks);
            }
            catch (Exception exception) when (!IsCriticalException(exception))
            {
                logger.LogDebug(exception, "Durability warning sample ignored a non-critical waiter error while cleaning up startup failure.");
            }

            if (openedTransaction is not null)
            {
                await openedTransaction.DisposeAsync();
            }

            if (openedConnection is not null)
            {
                await openedConnection.DisposeAsync();
            }

            throw;
        }
    }

    public async Task<DemoDurabilityWarningStatus> Stop(CancellationToken cancellationToken)
    {
        SqlConnection? connectionToDispose;
        SqlTransaction? transactionToDispose;
        CancellationTokenSource? cancellationToDispose;
        Task[] tasksToAwait;

        lock (this.sync)
        {
            connectionToDispose = this.connection;
            transactionToDispose = this.transaction;
            cancellationToDispose = this.cancellation;
            tasksToAwait = this.waitingTasks;
            this.connection = null;
            this.transaction = null;
            this.cancellation = null;
            this.waitingTasks = [];
            this.queuedWorkerCount = 0;
            this.startedAt = null;
        }

        cancellationToDispose?.Cancel();

        if (tasksToAwait.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasksToAwait).WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (!IsCriticalException(exception))
            {
                logger.LogWarning(exception, "Durability warning sample waiter tasks stopped with an error.");
            }
        }

        if (transactionToDispose is not null)
        {
            try
            {
                await transactionToDispose.RollbackAsync(cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                logger.LogDebug(exception, "Durability warning sample rollback was skipped because the transaction was already completed.");
            }
            finally
            {
                await transactionToDispose.DisposeAsync();
            }
        }

        if (connectionToDispose is not null)
        {
            await connectionToDispose.DisposeAsync();
        }

        cancellationToDispose?.Dispose();
        return this.Status();
    }

    public async ValueTask DisposeAsync()
    {
        lock (this.sync)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await this.Stop(timeout.Token);
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            logger.LogWarning(exception, "Durability warning sample failed to stop cleanly during disposal.");
        }
    }

    private static bool IsCriticalException(Exception exception)
        => exception is OutOfMemoryException or
            StackOverflowException or
            AccessViolationException or
            AppDomainUnloadedException or
            BadImageFormatException or
            CannotUnloadAppDomainException or
            InvalidProgramException;
}

public sealed record DemoDurabilityWarningStatus(
    bool IsRunning,
    int WorkerCount,
    DateTimeOffset? StartedAt,
    string Message);
