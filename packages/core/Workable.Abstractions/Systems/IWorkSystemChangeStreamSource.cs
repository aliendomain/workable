namespace Workable;

internal interface IWorkSystemChangeStreamSource
{
    IWorkChangeStream Changes { get; }
}
