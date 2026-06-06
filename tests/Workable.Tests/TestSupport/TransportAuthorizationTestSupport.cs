using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

internal static class TransportAuthorizationTestSupport
{
    public static IReadOnlyList<string> ReadGroups { get; } = ["transport.read"];

    public static IReadOnlyList<string> OperateGroups { get; } = ["transport.operate"];

    public static IReadOnlyList<string> DiagnosticsGroups { get; } = ["transport.diagnostics"];

    public static IReadOnlyList<string> ControlSystemGroups { get; } = ["transport.control"];

    public static IReadOnlyList<string> ReadAllWorkGroups { get; } = ["transport.read-all"];

    public static IReadOnlyList<string> OperateAllWorkGroups { get; } = ["transport.operate-all"];

    public static IReadOnlyList<string> SystemAdministratorGroups { get; } = ["transport.system-admin"];

    public static IReadOnlyList<string> WorkAdministratorGroups { get; } = ["transport.work-admin"];

    public static IServiceCollection AddTransportTestAuthorization(
        this IServiceCollection services,
        IEnumerable<string>? groups = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IWorkAuthorizationGroupProvider>(_ => new FixedWorkAuthorizationGroupProvider(groups));
        return services;
    }

    public static IWorkSystemSession CreateTransportSession(
        IWorkSystem system,
        WorkInvocationChannel channel = WorkInvocationChannel.DotNet,
        WorkActor? actor = null,
        string description = "Create transport test session.")
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.CreateSession(WorkRequestContext.Create(
            channel,
            actor ?? CreateActor(),
            description));
    }

    public static ClaimsPrincipal CreateTransportPrincipal(
        string id = "transport-user-1",
        string name = "Transport User",
        string email = "transport.user@example.test",
        IEnumerable<string>? groups = null)
        => new(new ClaimsIdentity(
            CreateClaims(id, name, email, groups),
            "Test"));

    public static WorkActor CreateActor(
        string id = "transport-user-1",
        string name = "Transport User",
        string email = "transport.user@example.test")
        => new(id, name, email);

    public static void ConfigureTransportSystemAuthorization(this IWorkSystemBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureAuthorization(authorization => authorization
            .SystemAdministrators(SystemAdministratorGroups.ToArray())
            .WorkAdministrators(WorkAdministratorGroups.ToArray())
            .AllowDiagnosticsToGroups(DiagnosticsGroups.ToArray())
            .AllowControlSystemToGroups(ControlSystemGroups.ToArray())
            .AllowReadAllWorkToGroups(ReadAllWorkGroups.ToArray())
            .AllowOperateAllWorkToGroups(OperateAllWorkGroups.ToArray()));
    }

    public static void AddAuthorizedTransportWork(
        this IWorkSystemBuilder builder,
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execute);

        builder.AddWork(
            definition,
            execute,
            configure,
            authorize => authorize.RequireGroups(ReadGroups, OperateGroups));
    }

    private sealed class FixedWorkAuthorizationGroupProvider(IEnumerable<string>? groups) : IWorkAuthorizationGroupProvider
    {
        private readonly IReadOnlySet<string> groups = new HashSet<string>(
            groups ?? DefaultGroups(),
            StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> GetGroups(WorkActor actor, string? systemName)
            => actor == WorkActor.Unknown
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : this.groups;
    }

    private static IEnumerable<Claim> CreateClaims(
        string id,
        string name,
        string email,
        IEnumerable<string>? groups)
    {
        yield return new Claim(ClaimTypes.NameIdentifier, id);
        yield return new Claim(ClaimTypes.Name, name);
        yield return new Claim(ClaimTypes.Email, email);

        foreach (var group in groups ?? DefaultGroups())
        {
            yield return new Claim("groups", group);
        }
    }

    private static IEnumerable<string> DefaultGroups()
        => ReadGroups
            .Concat(OperateGroups)
            .Concat(DiagnosticsGroups)
            .Concat(ControlSystemGroups)
            .Concat(ReadAllWorkGroups)
            .Concat(OperateAllWorkGroups)
            .Concat(SystemAdministratorGroups)
            .Concat(WorkAdministratorGroups);
}
