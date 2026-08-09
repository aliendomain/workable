using System.Collections.Concurrent;
using Workable;

namespace Workable.Tests;

internal sealed class TestExecutionDiagnosticsRepository : IWorkExecutionDiagnosticsRepository
{
    private readonly ConcurrentDictionary<Guid, WorkExecutionDiagnosticCaptureRule> rules = [];

    public WorkExecutionDiagnosticQueryResult QueryResult { get; set; } = new([]);

    public WorkExecutionDiagnosticArtifact? Artifact { get; set; }

    public WorkExecutionDiagnosticCriteria? LastCriteria { get; private set; }

    public WorkExecutionDiagnosticGetRequest? LastGetRequest { get; private set; }

    public Task Initialize(WorkExecutionDiagnosticsInitializationContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

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
        => Task.FromResult<IReadOnlyList<WorkExecutionDiagnosticCaptureRule>>([.. this.rules.Values]);

    public Task UpsertCaptureRule(WorkExecutionDiagnosticCaptureRule rule, int maximumActiveRules, CancellationToken cancellationToken = default)
    {
        this.rules[rule.Id] = rule;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteCaptureRule(
        WorkExecutionDiagnosticCaptureRuleDeleteRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(this.rules.TryRemove(request.RuleId, out _));
}
