using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

internal static class WorkableTestServiceCollectionExtensions
{
    public static IServiceCollection AddWorkableSystem(
        this IServiceCollection services,
        Action<IWorkSystemBuilder> configure)
        => global::Workable.WorkableServiceCollectionExtensions.AddWorkableSystem(
            services,
            builder =>
            {
                builder.RequireAuthorization(false);
                configure(builder);
            });

    public static IServiceCollection AddWorkableSystem(
        this IServiceCollection services,
        string? name,
        Action<IWorkSystemBuilder> configure)
        => global::Workable.WorkableServiceCollectionExtensions.AddWorkableSystem(
            services,
            name,
            builder =>
            {
                builder.RequireAuthorization(false);
                configure(builder);
            });
}
