namespace Workable;
public sealed record WorkMessage(
    string Code,
    WorkMessageSeverity Severity,
    string Text,
    string? Target = null,
    IReadOnlyDictionary<string, object?>? Metadata = null)
{
    public static WorkMessage Info(string code, string text, string? target = null)
        => new(code, WorkMessageSeverity.Info, text, target);

    public static WorkMessage Warning(string code, string text, string? target = null)
        => new(code, WorkMessageSeverity.Warning, text, target);

    public static WorkMessage Error(string code, string text, string? target = null)
        => new(code, WorkMessageSeverity.Error, text, target);
}
