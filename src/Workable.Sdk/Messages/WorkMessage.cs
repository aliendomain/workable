namespace Workable;
public sealed record WorkMessage(
    string Code,
    WorkMessageSeverity Severity,
    string Text,
    string? Target = null,
    IReadOnlyDictionary<string, object?>? Metadata = null)
{
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    public static WorkMessage Trace(string code, string text, string? target = null)
        => new(code, WorkMessageSeverity.Trace, text, target);

    public static WorkMessage Debug(string code, string text, string? target = null)
        => new(code, WorkMessageSeverity.Debug, text, target);

    public static WorkMessage Information(string code, string text, string? target = null)
        => new(code, WorkMessageSeverity.Information, text, target);

    public static WorkMessage Info(string code, string text, string? target = null)
        => Information(code, text, target);

    public static WorkMessage Warning(string code, string text, string? target = null)
        => new(code, WorkMessageSeverity.Warning, text, target);

    public static WorkMessage Error(string code, string text, string? target = null)
        => new(code, WorkMessageSeverity.Error, text, target);

    public static WorkMessage Critical(string code, string text, string? target = null)
        => new(code, WorkMessageSeverity.Critical, text, target);
}
