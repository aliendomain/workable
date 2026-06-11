namespace Workable;

/// <summary>
/// Represents an active profile scope handle that records duration until disposed.
/// </summary>
public interface IWorkProfileScope : IDisposable
{
    /// <summary>
    /// Records an optional result payload for scopes that support result capture before the scope ends.
    /// </summary>
    /// <param name="context">Optional structured result context captured under the scope. Timing scopes currently ignore this value.</param>
    void SetResult(object? context = null);
}
