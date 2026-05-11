using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;
public enum WorkStartPolicy
{
    DoNotStart,
    StartAndReturnAfterAccepted,
    StartAndReturnAfterStarted,
    StartAndReturnAfterCompleted,
}
