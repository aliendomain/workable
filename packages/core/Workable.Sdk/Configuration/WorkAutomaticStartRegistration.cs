namespace Workable;

internal sealed record WorkAutomaticStartRegistration(
    int InstanceCount,
    Func<IServiceProvider, WorkInput?> InputFactory)
{
    /// <summary>
    /// Creates an automatic-start registration with normalized instance count.
    /// </summary>
    /// <param name="instanceCount">The requested number of startup instances to queue.</param>
    /// <param name="inputFactory">The factory that creates each startup input payload.</param>
    /// <returns>The normalized automatic-start registration.</returns>
    public static WorkAutomaticStartRegistration Create(
        int instanceCount,
        Func<IServiceProvider, WorkInput?> inputFactory)
    {
        ArgumentNullException.ThrowIfNull(inputFactory);

        return new WorkAutomaticStartRegistration(
            Math.Max(1, instanceCount),
            inputFactory);
    }
}
