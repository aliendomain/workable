namespace Workable;

/// <summary>
/// Represents an active profile scope that records duration until disposed.
/// </summary>
public interface IWorkProfileScope : IDisposable
{
    /// <summary>
    /// Records an optional result payload for the scope before it ends.
    /// </summary>
    /// <param name="context">Optional structured result context captured under the scope.</param>
    void SetResult(object? context = null);
}
