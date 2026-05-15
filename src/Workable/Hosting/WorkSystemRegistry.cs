using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Workable;
internal sealed class WorkSystemRegistry : IWorkSystemRegistry
{
    private readonly IReadOnlyDictionary<WorkSystemId, IWorkSystem> byId;
    private readonly IReadOnlyDictionary<string, IWorkSystem> byName;

    public WorkSystemRegistry(
        IServiceProvider services,
        IEnumerable<WorkSystemRegistration> registrations,
        IEnumerable<WorkContribution> contributions,
        IEnumerable<WorkDefinitionSourceContribution> workDefinitionSourceContributions,
        IEnumerable<StartupWorkSourceContribution> startupWorkSourceContributions,
        IEnumerable<WorkableRegistrationOptions> options)
    {
        var hostShutdownTimeout = TryResolveHostShutdownTimeout(services);
        var globalExceptionClassifiers = options
            .SelectMany(option => option.ExceptionClassifiers)
            .ToList();
        var systems = registrations
            .Select(registration => new InMemoryWorkSystem(
                registration,
                ComposeWork(registration, contributions),
                ComposeWorkDefinitionSourceFactories(registration, workDefinitionSourceContributions),
                ComposeStartupWorkSourceFactories(registration, startupWorkSourceContributions),
                services,
                registration.ShutdownGracePeriod.Resolve(hostShutdownTimeout),
                globalExceptionClassifiers))
            .Cast<IWorkSystem>()
            .ToList();

        if (systems.Count == 0)
        {
            throw new InvalidOperationException("At least one Workable system must be registered.");
        }

        var defaultSystems = systems.Where(system => system.Name is null).ToList();
        if (defaultSystems.Count > 1)
        {
            throw new InvalidOperationException("Only one unnamed default Workable system can be registered.");
        }

        var namedSystems = systems
            .Select(system => (System: system, Name: system.Name ?? string.Empty))
            .Where(system => !string.IsNullOrWhiteSpace(system.Name))
            .ToList();

        var duplicateNames = namedSystems
            .GroupBy(system => system.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicateNames.Count > 0)
        {
            throw new InvalidOperationException($"Workable system names must be unique. Duplicate names: {string.Join(", ", duplicateNames)}.");
        }

        this.Systems = systems;
        this.Default = defaultSystems.SingleOrDefault() ?? systems[0];
        this.byId = systems.ToDictionary(system => system.Id);
        this.byName = namedSystems
            .ToDictionary(system => system.Name, system => system.System, StringComparer.OrdinalIgnoreCase);
    }

    public IWorkSystem Default { get; }

    public IReadOnlyCollection<IWorkSystem> Systems { get; }

    public bool TryGet(WorkSystemId id, [NotNullWhen(true)] out IWorkSystem? workSystem)
        => this.byId.TryGetValue(id, out workSystem);

    public bool TryGet(string name, [NotNullWhen(true)] out IWorkSystem? workSystem)
        => this.byName.TryGetValue(name, out workSystem);

    private static IReadOnlyList<RegisteredWork> ComposeWork(WorkSystemRegistration registration, IEnumerable<WorkContribution> contributions)
    {
        var registered = registration.IncludeContributedWork
            ? contributions
                .Where(contribution => IsContributionIncluded(registration.Name, contribution.SystemName))
                .Select(contribution => new RegisteredWork(
                    contribution.Definition,
                    contribution.ExecutorFactory,
                    contribution.ExceptionClassifiers,
                    contribution.AutomaticStarts,
                    contribution.Initializers))
            : [];

        return [.. registration.Work.Concat(registered)];
    }

    private static bool IsContributionIncluded(string? systemName, string? contributionSystemName)
        => string.IsNullOrWhiteSpace(contributionSystemName) ||
            string.Equals(systemName, contributionSystemName, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<Func<IServiceProvider, IWorkDefinitionSource>> ComposeWorkDefinitionSourceFactories(
        WorkSystemRegistration registration,
        IEnumerable<WorkDefinitionSourceContribution> contributions)
    {
        var contributed = registration.IncludeContributedWork
            ? contributions
                .Where(contribution => IsContributionIncluded(registration.Name, contribution.SystemName))
                .Select(contribution => contribution.SourceFactory)
            : [];

        return [.. registration.WorkDefinitionSourceFactories.Concat(contributed)];
    }

    private static IReadOnlyList<Func<IServiceProvider, IStartupWorkSource>> ComposeStartupWorkSourceFactories(
        WorkSystemRegistration registration,
        IEnumerable<StartupWorkSourceContribution> contributions)
    {
        var contributed = registration.IncludeContributedWork
            ? contributions
                .Where(contribution => IsContributionIncluded(registration.Name, contribution.SystemName))
                .Select(contribution => contribution.SourceFactory)
            : [];

        return [.. registration.StartupWorkSourceFactories.Concat(contributed)];
    }

    private static TimeSpan? TryResolveHostShutdownTimeout(IServiceProvider services)
    {
        var hostOptionsType = Type.GetType("Microsoft.Extensions.Hosting.HostOptions, Microsoft.Extensions.Hosting");
        var optionsTypeDefinition = Type.GetType("Microsoft.Extensions.Options.IOptions`1, Microsoft.Extensions.Options");
        if (hostOptionsType is null || optionsTypeDefinition is null)
        {
            return null;
        }

        var optionsServiceType = optionsTypeDefinition.MakeGenericType(hostOptionsType);
        var options = services.GetService(optionsServiceType);
        if (options is null)
        {
            return null;
        }

        var value = optionsServiceType.GetProperty("Value")?.GetValue(options);
        if (value is null)
        {
            return null;
        }

        var shutdownTimeout = hostOptionsType.GetProperty("ShutdownTimeout")?.GetValue(value);
        return shutdownTimeout is TimeSpan timeout ? timeout : null;
    }
}
