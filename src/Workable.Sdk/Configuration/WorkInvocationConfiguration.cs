namespace Workable;

public sealed record WorkInvocationConfiguration
{
    private static readonly IReadOnlySet<WorkInvocationChannel> DefaultChannels =
        new HashSet<WorkInvocationChannel>
        {
            WorkInvocationChannel.InProcess,
            WorkInvocationChannel.HttpApi,
        };

    public static WorkInvocationConfiguration Default { get; } = new()
    {
        AllowedChannels = DefaultChannels,
    };

    public IReadOnlySet<WorkInvocationChannel> AllowedChannels { get; init; } = DefaultChannels;

    public bool Allows(WorkInvocationChannel channel)
        => this.AllowedChannels.Contains(channel);

    public static WorkInvocationConfiguration Allow(params WorkInvocationChannel[] channels)
        => new()
        {
            AllowedChannels = channels.ToHashSet(),
        };

    public WorkInvocationConfiguration AllowAdditional(params WorkInvocationChannel[] channels)
    {
        ArgumentNullException.ThrowIfNull(channels);

        return this with
        {
            AllowedChannels = this.AllowedChannels.Concat(channels).ToHashSet(),
        };
    }
}
