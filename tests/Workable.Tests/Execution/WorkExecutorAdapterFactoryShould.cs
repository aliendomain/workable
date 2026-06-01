using Workable;

namespace Workable.Tests;

[Trait("Category", "Execution")]
public sealed class WorkExecutorAdapterFactoryShould
{
    [Fact]
    public void RejectUnsupportedExecutorTypes()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => WorkExecutorAdapterFactory.ThrowIfUnsupported(typeof(NotAnExecutor)));

        Assert.Contains(UnsupportedTypeName, exception.Message);
        Assert.Contains("must implement", exception.Message);
    }

    [Fact]
    public void RejectUnsupportedExecutorInstances()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => WorkExecutorAdapterFactory.Create(new NotAnExecutor()));

        Assert.Contains(UnsupportedTypeName, exception.Message);
        Assert.Contains("must implement", exception.Message);
    }

    [Fact]
    public void ReturnRawExecutorInstancesWithoutWrapping()
    {
        var executor = new RawExecutor();

        var adapted = WorkExecutorAdapterFactory.Create(executor);

        Assert.Same(executor, adapted);
    }

    private static string UnsupportedTypeName => typeof(NotAnExecutor).FullName ?? nameof(NotAnExecutor);

    private sealed class NotAnExecutor;

    private sealed class RawExecutor : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}
