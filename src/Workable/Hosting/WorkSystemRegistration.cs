using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Workable;
internal sealed record WorkSystemRegistration(
    WorkSystemId Id,
    string? Name,
    IReadOnlyList<RegisteredWork> Work,
    IReadOnlyList<Func<IServiceProvider, IWorkDefinitionSource>> WorkDefinitionSourceFactories,
    IReadOnlyList<Func<IServiceProvider, IStartupWorkSource>> StartupWorkSourceFactories,
    IReadOnlyList<WorkExceptionClassifier> ExceptionClassifiers,
    bool IncludeContributedWork,
    bool RequiresAuthorization,
    WorkSystemAuthorizationConfiguration Authorization,
    bool StartWithHost,
    WorkSystemShutdownGracePeriod ShutdownGracePeriod,
    WorkSystemRetentionConfiguration Retention,
    WorkSystemCapacityConfiguration Capacity);
