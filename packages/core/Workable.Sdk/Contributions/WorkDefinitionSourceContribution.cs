namespace Workable;

internal sealed record WorkDefinitionSourceContribution(
    string? SystemName,
    Func<IServiceProvider, IWorkDefinitionSource> SourceFactory);
