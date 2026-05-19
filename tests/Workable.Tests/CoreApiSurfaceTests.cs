using System.Reflection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "CoreApi")]
public sealed class CoreApiSurfaceTests
{
    [Fact]
    public void ConsumerContractsLiveInAbstractionsAssembly()
    {
        Assert.Equal("Workable.Abstractions", typeof(IWorkSystem).Assembly.GetName().Name);
        Assert.Equal("Workable.Abstractions", typeof(IWorkQueueService).Assembly.GetName().Name);
        Assert.Equal("Workable.Abstractions", typeof(WorkerSnapshot).Assembly.GetName().Name);
    }

    [Fact]
    public void AspNetCoreOriginAdapterDoesNotReferenceRuntimeHostPackage()
    {
        var references = typeof(HttpContextDotNetWorkOriginProvider)
            .Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToList();

        Assert.Contains("Workable.Abstractions", references);
        Assert.DoesNotContain("Workable", references);
    }

    [Fact]
    public void WorkablePublicApiDoesNotDeclareAsyncSuffixedMembers()
    {
        var declaredMembers = typeof(IWorkSystem).Assembly
            .GetExportedTypes()
            .SelectMany(type => type.GetMembers(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.DeclaredOnly))
            .Where(member => member.MemberType is MemberTypes.Method or MemberTypes.Property or MemberTypes.Event)
            .Select(member => $"{member.DeclaringType?.FullName}.{member.Name}")
            .ToList();

        Assert.DoesNotContain(declaredMembers, memberName => memberName.EndsWith("Async", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkQueueExposesRawAndTypedEnqueueMethods()
    {
        var methods = typeof(IWorkQueueService)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == nameof(IWorkQueueService.Enqueue))
            .ToList();

        Assert.Contains(methods, method =>
            !method.IsGenericMethodDefinition &&
            method.GetParameters()[0].ParameterType == typeof(WorkDefinitionId) &&
            method.GetParameters()[1].ParameterType == typeof(WorkInput));
        Assert.Contains(methods, method =>
            !method.IsGenericMethodDefinition &&
            method.GetParameters()[0].ParameterType == typeof(string) &&
            method.GetParameters()[1].ParameterType == typeof(WorkInput));
        Assert.Contains(methods, method =>
            method.IsGenericMethodDefinition &&
            method.GetParameters()[0].ParameterType == typeof(WorkDefinitionId) &&
            method.GetParameters()[1].ParameterType.IsGenericParameter);
        Assert.Contains(methods, method =>
            method.IsGenericMethodDefinition &&
            method.GetParameters()[0].ParameterType == typeof(string) &&
            method.GetParameters()[1].ParameterType.IsGenericParameter);
    }

    [Fact]
    public void WorkerHandleExposesRawAndTypedCompletionMethods()
    {
        var methods = typeof(IWorkerHandle)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == nameof(IWorkerHandle.WaitForCompletion))
            .ToList();

        Assert.Contains(methods, method =>
            !method.IsGenericMethodDefinition &&
            method.ReturnType == typeof(Task<WorkCompletion>));
        Assert.Contains(methods, method =>
            method.IsGenericMethodDefinition &&
            method.ReturnType.IsGenericType &&
            method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>) &&
            method.ReturnType.GetGenericArguments()[0].IsGenericType &&
            method.ReturnType.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(WorkCompletion<>));
    }
}
