using System.Reflection;
using System.Runtime.CompilerServices;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Systems")]
public sealed class InMemoryWorkSystemBranchShould
{
    [Fact]
    public void ClassifyEveryCriticalCleanupAndLifecycleException()
    {
        var cleanupCritical = new Exception[]
        {
            new OperationCanceledException(),
            new OutOfMemoryException(),
            new StackOverflowException(),
            new AccessViolationException(),
            new AppDomainUnloadedException(),
            new BadImageFormatException(),
            new CannotUnloadAppDomainException(),
            (Exception)RuntimeHelpers.GetUninitializedObject(typeof(ThreadAbortException)),
        };
        Assert.All(cleanupCritical, exception =>
            Assert.False(Invoke<bool>("ShouldCaptureCleanupException", exception)));
        Assert.True(Invoke<bool>("ShouldCaptureCleanupException", new InvalidOperationException()));

        var lifecycleCritical = cleanupCritical
            .Where(exception => exception is not OperationCanceledException)
            .Append(new InvalidProgramException());
        Assert.All(lifecycleCritical, exception =>
            Assert.False(Invoke<bool>("IsNonCriticalException", exception)));
        Assert.True(Invoke<bool>("IsNonCriticalException", new OperationCanceledException()));
        Assert.True(Invoke<bool>("IsNonCriticalException", new InvalidOperationException()));
    }

    private static T Invoke<T>(string name, params object?[] arguments)
        => (T)typeof(InMemoryWorkSystem)
            .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, arguments)!;
}
