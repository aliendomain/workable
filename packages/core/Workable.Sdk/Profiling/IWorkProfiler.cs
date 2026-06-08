using System.Runtime.CompilerServices;

namespace Workable;

/// <summary>
/// Contributes entries to the currently active worker profile.
/// </summary>
public interface IWorkProfiler
{
    /// <summary>
    /// Adds an informational profile node without timing scope.
    /// </summary>
    /// <param name="name">The profile node label.</param>
    /// <param name="context">Optional structured context captured under the profile node.</param>
    void AddInfo(string name, object? context = null);

    /// <summary>
    /// Starts a timed profile scope.
    /// </summary>
    /// <param name="name">The profile node label.</param>
    /// <param name="context">Optional structured context captured under the profile node.</param>
    /// <returns>A scope that records elapsed time until disposed.</returns>
    IWorkProfileScope StartTiming(string name, object? context = null);

    /// <summary>
    /// Creates a logical profile scope without the method-specific label conventions.
    /// </summary>
    /// <param name="name">The profile node label.</param>
    /// <param name="context">Optional structured context captured under the profile node.</param>
    /// <returns>A scope that can later capture a result and ends when disposed.</returns>
    IWorkProfileScope CreateScope(string name, object? context = null);

    /// <summary>
    /// Creates a method-style profile scope with explicit declaring type and method name.
    /// </summary>
    /// <param name="type">The declaring type associated with the profiled method.</param>
    /// <param name="methodName">The method name to show in the profile tree.</param>
    /// <param name="context">Optional structured input context captured under the profile node.</param>
    /// <param name="label">The label used for the captured input context.</param>
    /// <returns>A scope that can later capture a result and ends when disposed.</returns>
    IWorkProfileScope CreateMethodScope(
        Type type,
        string methodName,
        object? context = null,
        string label = "Input");

    /// <summary>
    /// Creates a method-style profile scope using the supplied generic type and caller member name.
    /// </summary>
    /// <typeparam name="T">The declaring type associated with the profiled method.</typeparam>
    /// <param name="context">Optional structured input context captured under the profile node.</param>
    /// <param name="label">The label used for the captured input context.</param>
    /// <param name="methodName">The caller member name used as the method name.</param>
    /// <returns>A scope that can later capture a result and ends when disposed.</returns>
    IWorkProfileScope CreateMethodScope<T>(
        object? context = null,
        string label = "Input",
        [CallerMemberName] string methodName = "");
}
