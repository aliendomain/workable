namespace Workable;

/// <summary>
/// Performs feature bootstrap work when a Workable host builds its system registry.
/// </summary>
public interface IWorkableBootstrapper
{
    /// <summary>
    /// Initializes the feature for the current host.
    /// </summary>
    void Initialize();
}
