namespace Workable;

internal sealed class AuthorizedWorkIterationStatusStream(
    WorkIterationStatusStream inner,
    IReadOnlySet<string> readableDefinitionNames) : IWorkIterationStatusStream
{
    public IWorkIterationStatusSubscription Subscribe(
        WorkerIterationReference iteration,
        long afterSequence = 0)
    {
        if (!inner.TryGetDefinitionName(iteration, out var definitionName) ||
            string.IsNullOrWhiteSpace(definitionName) ||
            !readableDefinitionNames.Contains(definitionName))
        {
            return EmptyWorkIterationStatusSubscription.Instance;
        }

        return inner.Subscribe(iteration, afterSequence);
    }
}
