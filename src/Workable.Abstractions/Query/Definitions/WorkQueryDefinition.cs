namespace Workable;

internal abstract record WorkQueryDefinition<TResult>(string Name)
    where TResult : IWorkQueryResult;
