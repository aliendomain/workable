using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;
public sealed record WorkIdempotencyConfiguration
{
    public static WorkIdempotencyConfiguration Default { get; } = new();

    public bool IsEnabled { get; init; }

    public WorkIdempotencyConflictPolicy ConflictPolicy { get; init; } = WorkIdempotencyConflictPolicy.RejectDuplicates;
}
