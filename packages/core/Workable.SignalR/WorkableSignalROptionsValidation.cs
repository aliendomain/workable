namespace Workable;

internal static class WorkableSignalROptionsValidation
{
    private static readonly TimeSpan MaximumTimerInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    public static void ThrowIfInvalidRealtime(WorkableSignalROptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ValidateTimerInterval(options.PublishInterval, nameof(options.PublishInterval));
        ValidateTimerInterval(options.DiagnosticsPublishInterval, nameof(options.DiagnosticsPublishInterval));
        ValidateTimerInterval(options.BatchTimeWindow, nameof(options.BatchTimeWindow));
        ValidateTimerInterval(options.LiveTimeWindow, nameof(options.LiveTimeWindow));
        ValidateTimerInterval(options.MinimumTimeWindow, nameof(options.MinimumTimeWindow));

        if (options.EventSubscriptionCapacity <= 0)
        {
            throw Invalid(nameof(options.EventSubscriptionCapacity), "must be greater than zero");
        }

        if (!Enum.IsDefined(options.EventOverflowBehavior))
        {
            throw Invalid(nameof(options.EventOverflowBehavior), "must be a defined overflow behavior");
        }

        if (options.EventMaxBatchSize <= 0)
        {
            throw Invalid(nameof(options.EventMaxBatchSize), "must be greater than zero");
        }

        if (options.MaximumSubscriptionsPerConnectionPerKind <= 0)
        {
            throw Invalid(nameof(options.MaximumSubscriptionsPerConnectionPerKind), "must be greater than zero");
        }

        if (options.MaximumSubscriptionsPerKind <= 0)
        {
            throw Invalid(nameof(options.MaximumSubscriptionsPerKind), "must be greater than zero");
        }

        if (options.MaximumSubscriptionsPerConnectionPerKind > options.MaximumSubscriptionsPerKind)
        {
            throw Invalid(
                nameof(options.MaximumSubscriptionsPerConnectionPerKind),
                $"must be no greater than {nameof(options.MaximumSubscriptionsPerKind)}");
        }

        if (options.MaximumEventFilterValuesPerField <= 0)
        {
            throw Invalid(nameof(options.MaximumEventFilterValuesPerField), "must be greater than zero");
        }

        if (options.MaximumEventFilterValueLength <= 0)
        {
            throw Invalid(nameof(options.MaximumEventFilterValueLength), "must be greater than zero");
        }
    }

    private static void ValidateTimerInterval(TimeSpan value, string propertyName)
    {
        if (value <= TimeSpan.Zero || value > MaximumTimerInterval)
        {
            throw Invalid(
                propertyName,
                $"must be greater than zero and no greater than {MaximumTimerInterval}");
        }
    }

    private static InvalidOperationException Invalid(string propertyName, string requirement)
        => new($"Workable SignalR option '{propertyName}' {requirement}.");
}
