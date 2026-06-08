namespace Workable;

/// <summary>
/// Controls which entry points may start a work definition.
/// </summary>
public sealed record WorkInvocationConfiguration
{
    private static readonly IReadOnlySet<WorkInvocationChannel> DefaultChannels =
        new HashSet<WorkInvocationChannel>
        {
            WorkInvocationChannel.InProcess,
            WorkInvocationChannel.HttpApi,
        };

    /// <summary>
    /// Gets the default invocation configuration, which allows in-process and HTTP API queueing.
    /// </summary>
    public static WorkInvocationConfiguration Default { get; } = new()
    {
        AllowedChannels = DefaultChannels,
    };

    /// <summary>
    /// Gets the channels currently allowed to start the definition.
    /// </summary>
    public IReadOnlySet<WorkInvocationChannel> AllowedChannels { get; init; } = DefaultChannels;

    /// <summary>
    /// Determines whether the supplied channel may start the definition.
    /// </summary>
    /// <param name="channel">The invocation channel to test.</param>
    /// <returns><see langword="true"/> when the channel is allowed; otherwise <see langword="false"/>.</returns>
    public bool Allows(WorkInvocationChannel channel)
        => this.AllowedChannels.Contains(channel);

    /// <summary>
    /// Creates an invocation configuration that allows only the supplied channels.
    /// </summary>
    /// <param name="channels">The channels to allow.</param>
    /// <returns>The created invocation configuration.</returns>
    public static WorkInvocationConfiguration Allow(params WorkInvocationChannel[] channels)
        => new()
        {
            AllowedChannels = channels.ToHashSet(),
        };

    /// <summary>
    /// Creates a copy of the configuration with additional allowed channels merged in.
    /// </summary>
    /// <param name="channels">The additional channels to allow.</param>
    /// <returns>The updated invocation configuration.</returns>
    public WorkInvocationConfiguration AllowAdditional(params WorkInvocationChannel[] channels)
    {
        ArgumentNullException.ThrowIfNull(channels);

        return this with
        {
            AllowedChannels = this.AllowedChannels.Concat(channels).ToHashSet(),
        };
    }
}
