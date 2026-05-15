using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Hosting")]
public sealed class AspNetCoreOriginTests
{
    [Fact]
    public async Task DirectDotNetQueueInsideAspNetCoreRequestCanUseHttpContextOriginWithoutWorkableHttpApi()
    {
        using var host = await CreateHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;

        var response = await client.PostAsync("/custom/queue", content: null);
        response.EnsureSuccessStatusCode();
        var workerId = await response.Content.ReadFromJsonAsync<Guid>();

        var worker = await system.Query.Worker(new WorkerId(workerId))
            ?? throw new InvalidOperationException("Expected worker.");

        Assert.Equal(WorkInvocationChannel.DotNet, worker.Origin.Channel);
        Assert.Equal("user-123", worker.Origin.Actor.Id);
        Assert.Equal("greya@example.test", worker.Origin.Actor.Email);
        Assert.Equal("Queue work 'direct.http' through .NET.", worker.Origin.Description);
        Assert.Equal("/custom/queue", worker.Origin.Url);
    }

    private static async Task<IHost> CreateHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.AddWork(
                            WorkDefinition.Create(
                                "direct.http",
                                configuration: WorkConfiguration.Default with
                                {
                                    Start = WorkStartConfiguration.DoNotStart,
                                }),
                            SuccessfulWork);
                    });
                    services.AddWorkableAspNetCoreOrigins();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.Use(async (context, next) =>
                    {
                        context.User = new ClaimsPrincipal(new ClaimsIdentity(
                            [
                                new Claim(ClaimTypes.NameIdentifier, "user-123"),
                                new Claim(ClaimTypes.Name, "Greya"),
                                new Claim(ClaimTypes.Email, "greya@example.test"),
                            ],
                            "Test"));
                        await next();
                    });
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/custom/queue", async (IWorkSystemRegistry registry) =>
                        {
                            var handle = await registry.Default.Queue.Enqueue("direct.http");
                            return handle.WorkerId?.Value ?? throw new InvalidOperationException("Expected worker.");
                        });
                    });
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());
}
