namespace Workable;

/// <summary>
/// Represents an automatic profiling operation that may still be active when a worker profile is finalized.
/// </summary>
public interface IWorkProfilePendingInstrumentation
{
    /// <summary>
    /// Finalizes the operation before the immutable profile snapshot is created.
    /// </summary>
    void FinalizeForProfileSnapshot();
}

/// <summary>
/// Coordinates registration and finalization of asynchronous automatic profiling operations.
/// </summary>
public interface IWorkProfilePendingInstrumentationRegistry
{
    /// <summary>
    /// Gets whether the profile still accepts pending instrumentation registrations.
    /// </summary>
    bool IsAcceptingPendingInstrumentation { get; }

    /// <summary>
    /// Enters the short registration window used to prevent snapshot publication races.
    /// </summary>
    bool TryEnterPendingInstrumentationRegistration();

    /// <summary>
    /// Registers an operation that must be finalized with the profile.
    /// </summary>
    void RegisterPendingInstrumentation(IWorkProfilePendingInstrumentation instrumentation);

    /// <summary>
    /// Exits the pending instrumentation registration window.
    /// </summary>
    void ExitPendingInstrumentationRegistration();

    /// <summary>
    /// Removes a completed operation from pending instrumentation.
    /// </summary>
    void UnregisterPendingInstrumentation(IWorkProfilePendingInstrumentation instrumentation);
}
