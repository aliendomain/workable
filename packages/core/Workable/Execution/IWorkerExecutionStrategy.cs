using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Workable;
internal interface IWorkerExecutionStrategy
{
    Task<WorkCompletion> Execute(WorkerRecord worker, CancellationToken cancellationToken);
}
