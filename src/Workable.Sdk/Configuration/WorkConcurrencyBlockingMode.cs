using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;
public enum WorkConcurrencyBlockingMode
{
    WhileExecutingPausedOrFailed,
    WhileExecutingOrPaused,
    WhileExecutingOrFailed,
    WhileExecuting,
}
