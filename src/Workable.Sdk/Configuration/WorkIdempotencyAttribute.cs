using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkIdempotencyAttribute : Attribute
{
    public WorkIdempotencyAttribute(
        bool isEnabled = true,
        WorkIdempotencyConflictPolicy conflictPolicy = WorkIdempotencyConflictPolicy.RejectDuplicates,
        WorkIdempotencyStorage storage = WorkIdempotencyStorage.Local)
    {
        this.Configuration = new WorkIdempotencyConfiguration
        {
            IsEnabled = isEnabled,
            Storage = storage,
            ConflictPolicy = conflictPolicy,
        };

        WorkConfigurationValidator.ThrowIfInvalid(WorkConfiguration.Default with { Idempotency = this.Configuration });
    }

    public WorkIdempotencyConfiguration Configuration { get; }
}
