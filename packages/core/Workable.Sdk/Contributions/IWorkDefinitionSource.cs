namespace Workable;

/// <summary>
/// Defines work registrations dynamically before a system catalog is frozen.
/// </summary>
/// <remarks>
/// Workable resolves and calls the source during system startup. Implementations should add definitions
/// through the supplied <see cref="IWorkDefinitionBuilder"/> and should not assume the system is already running.
/// </remarks>
public interface IWorkDefinitionSource
{
    /// <summary>
    /// Adds work definitions to the system that is currently starting.
    /// </summary>
    /// <param name="builder">
    /// The builder used to register definitions, executors, configuration, and work-level authorization
    /// for the current system.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that is canceled when system startup is being aborted before the catalog has finished loading.
    /// </param>
    /// <returns>A task that completes when the source has finished defining its work.</returns>
    Task DefineWork(IWorkDefinitionBuilder builder, CancellationToken cancellationToken = default);
}
