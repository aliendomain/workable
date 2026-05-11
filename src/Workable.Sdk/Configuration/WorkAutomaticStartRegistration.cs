namespace Workable;

internal sealed record WorkAutomaticStartRegistration(
    int InstanceCount,
    Func<IServiceProvider, WorkInput?> InputFactory)
{
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
