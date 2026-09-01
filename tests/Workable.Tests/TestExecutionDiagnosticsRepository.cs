using System.Collections.Concurrent;
using Workable;

namespace Workable.Tests;

internal sealed class TestExecutionDiagnosticsRepository : IWorkExecutionDiagnosticsRepository
{
    private readonly ConcurrentDictionary<Guid, WorkExecutionDiagnosticCaptureRule> rules = [];
    private int initializeCallCount;

    public WorkExecutionDiagnosticQueryResult QueryResult { get; set; } = new([]);

    public WorkExecutionDiagnosticArtifact? Artifact { get; set; }

    public Exception? QueryException { get; set; }

    public Exception? InitializeException { get; set; }

    public int InitializeCallCount => Volatile.Read(ref this.initializeCallCount);

    public Exception? ListCaptureRulesException { get; set; }

    public Exception? UpsertCaptureRuleException { get; set; }

    public WorkExecutionDiagnosticCriteria? LastCriteria { get; private set; }

    public WorkExecutionDiagnosticGetRequest? LastGetRequest { get; private set; }

    public Task Initialize(WorkExecutionDiagnosticsInitializationContext context, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref this.initializeCallCount);
        return this.InitializeException is null
            ? Task.CompletedTask
            : Task.FromException(this.InitializeException);
    }

    public Task BeginIteration(WorkExecutionDiagnosticIterationStart iteration, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task AppendLogs(IReadOnlyList<WorkExecutionDiagnosticLogRecord> logs, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task CompleteIteration(WorkExecutionDiagnosticIterationCompletion completion, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<int> DeleteExpired(WorkExecutionDiagnosticsExpirationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    public Task<WorkExecutionDiagnosticQueryResult> Query(WorkExecutionDiagnosticCriteria criteria, CancellationToken cancellationToken = default)
    {
        this.LastCriteria = criteria;
        if (this.QueryException is not null)
        {
            throw this.QueryException;
        }

        return Task.FromResult(this.QueryResult);
    }

    public Task<WorkExecutionDiagnosticArtifact?> Get(WorkExecutionDiagnosticGetRequest request, CancellationToken cancellationToken = default)
    {
        this.LastGetRequest = request;
        return Task.FromResult(this.Artifact);
    }

    public Task<IReadOnlyList<WorkExecutionDiagnosticCaptureRule>> ListCaptureRules(
        WorkExecutionDiagnosticsInitializationContext context,
        CancellationToken cancellationToken = default)
        => this.ListCaptureRulesException is null
            ? Task.FromResult<IReadOnlyList<WorkExecutionDiagnosticCaptureRule>>([.. this.rules.Values])
            : Task.FromException<IReadOnlyList<WorkExecutionDiagnosticCaptureRule>>(this.ListCaptureRulesException);

    public Task UpsertCaptureRule(WorkExecutionDiagnosticCaptureRule rule, int maximumActiveRules, CancellationToken cancellationToken = default)
    {
        if (this.UpsertCaptureRuleException is not null)
        {
            throw this.UpsertCaptureRuleException;
        }

        foreach (var existing in this.rules.Values.Where(existing =>
            existing.Id != rule.Id &&
            StringComparer.OrdinalIgnoreCase.Equals(existing.DefinitionName, rule.DefinitionName)))
        {
            this.rules.TryRemove(existing.Id, out _);
        }
        this.rules[rule.Id] = rule;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteCaptureRule(
        WorkExecutionDiagnosticCaptureRuleDeleteRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(this.rules.TryRemove(request.RuleId, out _));
}
