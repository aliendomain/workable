using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Mcp")]
public sealed class WorkableMcpServerExtensionsBranchShould
{
    [Fact]
    public void ResolveSystemNameOnlyFromTheCurrentEndpointMetadata()
    {
        Assert.Null(GetSystemName(new ServiceCollection().BuildServiceProvider()));

        var accessor = new HttpContextAccessor();
        using var services = new ServiceCollection()
            .AddSingleton<IHttpContextAccessor>(accessor)
            .BuildServiceProvider();
        Assert.Null(GetSystemName(services));

        accessor.HttpContext = new DefaultHttpContext();
        Assert.Null(GetSystemName(services));
        accessor.HttpContext.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(),
            "without-workable-metadata"));
        Assert.Null(GetSystemName(services));

        var metadataType = typeof(WorkableMcpServerExtensions).GetNestedType(
            "WorkableMcpEndpointMetadata",
            BindingFlags.NonPublic);
        Assert.NotNull(metadataType);
        var metadata = Activator.CreateInstance(metadataType, "background");
        Assert.NotNull(metadata);
        accessor.HttpContext.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(metadata),
            "with-workable-metadata"));
        Assert.Equal("background", GetSystemName(services));
    }

    private static string? GetSystemName(IServiceProvider services)
    {
        var method = typeof(WorkableMcpServerExtensions).GetMethod(
            "GetSystemName",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string?)method.Invoke(null, [services]);
    }
}
