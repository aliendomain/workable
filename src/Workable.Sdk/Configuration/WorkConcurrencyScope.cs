using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;
public enum WorkConcurrencyScope
{
    PerDefinition,
    PerSubject,
    PerConcurrencyKey,
}
