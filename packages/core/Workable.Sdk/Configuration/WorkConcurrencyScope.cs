using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Defines how Workable groups workers when enforcing concurrency capacity.
/// </summary>
public enum WorkConcurrencyScope
{
    /// <summary>
    /// Shares capacity across all workers of the same definition.
    /// </summary>
    PerDefinition,

    /// <summary>
    /// Shares capacity only across workers that have the same <see cref="WorkSubjectId"/>.
    /// </summary>
    PerSubject,

    /// <summary>
    /// Shares capacity only across workers that have the same <see cref="WorkConcurrencyKey"/>.
    /// </summary>
    PerConcurrencyKey,
}
