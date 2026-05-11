using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;
public enum WorkRetryBackoff
{
    None,
    Exponential,
}
