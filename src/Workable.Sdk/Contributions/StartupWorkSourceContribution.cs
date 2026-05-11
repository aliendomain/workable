namespace Workable;

internal sealed record StartupWorkSourceContribution(
    string? SystemName,
    Func<IServiceProvider, IStartupWorkSource> SourceFactory);
