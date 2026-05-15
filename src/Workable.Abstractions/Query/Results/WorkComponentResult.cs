namespace Workable;

public sealed record WorkComponentResult(
    string Status,
    object? Data = null,
    string? Error = null);
