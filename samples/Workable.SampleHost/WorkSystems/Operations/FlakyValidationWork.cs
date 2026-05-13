using Workable;

namespace Workable.SampleHost.Operations;

public sealed record FlakyValidationInput(
    string ScenarioName,
    bool ShouldFail,
    int WarningCount = 0);

public sealed record FlakyValidationOutput(
    string ScenarioName,
    bool Passed,
    DateTimeOffset CompletedAt);

[WorkMetadata("qa.validation.flaky", "Quality:Validation", "Can return warnings or a structured failure for UI testing.")]
public sealed class FlakyValidationWork : IWorkExecutor<FlakyValidationInput, FlakyValidationOutput>
{
    public Task<WorkExecutionResult<FlakyValidationOutput>> Execute(
        IWorkExecutionContext context,
        FlakyValidationInput input,
        CancellationToken cancellationToken)
    {
        var messages = Enumerable
            .Range(1, Math.Clamp(input.WarningCount, 0, 5))
            .Select(index => WorkMessage.Warning("qa.validation.warning", $"Synthetic warning {index}.", "warningCount"))
            .ToList();

        if (input.ShouldFail)
        {
            messages.Add(WorkMessage.Error("qa.validation.failed", "The sample validation was asked to fail.", "shouldFail"));
            return Task.FromResult(WorkExecutionResult<FlakyValidationOutput>.Failure(
                messages,
                new FlakyValidationOutput(input.ScenarioName, false, DateTimeOffset.UtcNow)));
        }

        messages.Add(WorkMessage.Info("qa.validation.passed", "The sample validation passed."));
        return Task.FromResult(WorkExecutionResult<FlakyValidationOutput>.Success(
            new FlakyValidationOutput(input.ScenarioName, true, DateTimeOffset.UtcNow),
            messages));
    }
}
