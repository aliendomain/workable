using Microsoft.Extensions.DependencyInjection;

namespace Workable;

internal sealed record WorkInitializationRegistration(
    WorkInitializationId Id,
    Type InitializerType,
    WorkInitializationTiming Timing,
    int? ExecutionOrder,
    Func<IServiceProvider, object> InitializerFactory)
{
    public bool IsTyped { get; } =
        InitializerType.GetInterfaces().Any(IsTypedInitializerInterface);

    public static WorkInitializationRegistration Create<TInitializer>(
        WorkInitializationTiming timing,
        int? executionOrder)
        where TInitializer : class
    {
        var initializerType = typeof(TInitializer);
        var isTyped = initializerType.GetInterfaces().Any(IsTypedInitializerInterface);
        if (!typeof(IWorkInitializer).IsAssignableFrom(initializerType) && !isTyped)
        {
            throw new InvalidOperationException(
                $"Initializer type '{initializerType.FullName}' must implement {nameof(IWorkInitializer)} or {nameof(IWorkInitializer<object>)}.");
        }

        if (timing == WorkInitializationTiming.OnceLazy && isTyped)
        {
            throw new InvalidOperationException(
                $"Initializer type '{initializerType.FullName}' cannot use {nameof(WorkInitializationTiming.OnceLazy)} because typed initializers depend on worker input.");
        }

        return new WorkInitializationRegistration(
            WorkInitializationId.New(),
            initializerType,
            timing,
            executionOrder,
            serviceProvider => serviceProvider.GetRequiredService<TInitializer>());
    }

    private static bool IsTypedInitializerInterface(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IWorkInitializer<>);
}
