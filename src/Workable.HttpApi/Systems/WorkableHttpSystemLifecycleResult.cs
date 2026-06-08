namespace Workable;

/// <summary>
/// Represents the compact HTTP response returned by lifecycle operations such as start.
/// </summary>
/// <param name="Name">The configured system name, or <see langword="null"/> for the default unnamed system.</param>
/// <param name="State">The resulting lifecycle state of the system.</param>
public sealed record WorkableHttpSystemLifecycleResult(
    string? Name,
    WorkSystemState State);
