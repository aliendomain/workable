using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Reflection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class WorkableEntraAuthorizationOptionsShould
{
    [Fact]
    public async Task BindOnlyWorkableIntegrationSettingsFromConfiguration()
    {
        using var configuration = new ConfigurationManager
        {
            ["AuthenticationScheme"] = "HostEntra",
            ["MapScopesToWorkableGroups"] = "false",
            ["MapAppRolesToWorkableGroups"] = "false",
            ["MapGroupsToWorkableGroups"] = "false",
        };
        var services = new ServiceCollection();

        services.AddWorkableEntraAuthorization(configuration);

        await using var provider = services.BuildServiceProvider();
        var workable = provider.GetRequiredService<IOptions<WorkableAspNetCoreAuthorizationOptions>>().Value;
        Assert.Equal("HostEntra", workable.TransportAuthenticationScheme);
        Assert.Equal(
            new WorkableAspNetCoreAuthorizationOptions().ActorIdClaimTypes,
            workable.ActorIdClaimTypes);
        Assert.Equal(new WorkableAspNetCoreAuthorizationOptions().GroupClaimTypes, workable.GroupClaimTypes);
        Assert.Equal(1000, Assert.Single(provider.GetServices<IWorkActorClaimsMapper>()).Order);
        Assert.Single(provider.GetServices<IWorkAuthorizationGroupClaimMapper>());
        Assert.Empty(provider.GetServices<IStartupFilter>());
    }

    [Fact]
    public async Task UseTheHostPrincipalAndDocumentedMappingsByDefault()
    {
        var services = new ServiceCollection();

        services.AddWorkableEntraAuthorization();

        await using var provider = services.BuildServiceProvider();
        var workable = provider.GetRequiredService<IOptions<WorkableAspNetCoreAuthorizationOptions>>().Value;
        Assert.Null(workable.TransportAuthenticationScheme);
        Assert.Equal(new WorkableAspNetCoreAuthorizationOptions().GroupClaimTypes, workable.GroupClaimTypes);
        Assert.Empty(workable.GroupClaimValueSeparatorsByClaimType);
        Assert.Equal([','], workable.GroupClaimValueSeparators);
        Assert.Single(provider.GetServices<IWorkActorClaimsMapper>());
        Assert.Single(provider.GetServices<IWorkAuthorizationGroupClaimMapper>());
        Assert.Empty(provider.GetServices<IStartupFilter>());
    }

    [Theory]
    [InlineData("MapScopesToWorkableGroups")]
    [InlineData("MapAppRolesToWorkableGroups")]
    [InlineData("MapGroupsToWorkableGroups")]
    public void RejectMalformedBooleanConfiguration(string key)
    {
        using var configuration = new ConfigurationManager
        {
            [key] = "not-a-boolean",
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddWorkableEntraAuthorization(configuration));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        Assert.Contains("'true' or 'false'", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AuthenticationScheme", "HostEntra")]
    [InlineData("MapScopesToWorkableGroups", "true")]
    [InlineData("MapAppRolesToWorkableGroups", "false")]
    [InlineData("MapGroupsToWorkableGroups", "true")]
    public void DetectEachSupportedConfigurationSettingIndependently(string key, string value)
    {
        using var configuration = new ConfigurationManager { [key] = value };

        var method = typeof(WorkableEntraAuthorizationOptions).GetMethod(
            "HasConfiguredValues",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected configuration detection helper.");
        Assert.True((bool)method.Invoke(null, [configuration])!);
        Assert.False((bool)method.Invoke(null, [new ConfigurationManager()])!);
    }

    [Fact]
    public async Task LeaveTheHostOwnedBearerSchemeUnchanged()
    {
        var onAuthenticationFailed = new Func<AuthenticationFailedContext, Task>(_ => Task.CompletedTask);
        var services = new ServiceCollection();
        services.AddSingleton<HostJwtBearerEvents>();
        services
            .AddAuthentication("HostEntra")
            .AddJwtBearer("HostEntra", jwt =>
            {
                jwt.Authority = "https://login.example.test/host/v2.0";
                jwt.Audience = "host-audience";
                jwt.MapInboundClaims = false;
                jwt.EventsType = typeof(HostJwtBearerEvents);
                jwt.Events.OnAuthenticationFailed = onAuthenticationFailed;
                jwt.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(17);
                jwt.TokenValidationParameters.NameClaimType = "host-name";
                jwt.TokenValidationParameters.RoleClaimType = "host-role";
                jwt.TokenValidationParameters.ValidAudiences = ["host-audience", "host-v1-audience"];
            });
        services.AddWorkableEntraAuthorization(options =>
            options.AuthenticationScheme = "HostEntra");

        await using var provider = services.BuildServiceProvider();
        var jwt = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("HostEntra");
        var schemes = await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        Assert.Equal("https://login.example.test/host/v2.0", jwt.Authority);
        Assert.Equal("host-audience", jwt.Audience);
        Assert.False(jwt.MapInboundClaims);
        Assert.Equal(typeof(HostJwtBearerEvents), jwt.EventsType);
        Assert.Same(onAuthenticationFailed, jwt.Events.OnAuthenticationFailed);
        Assert.Equal(TimeSpan.FromSeconds(17), jwt.TokenValidationParameters.ClockSkew);
        Assert.Equal("host-name", jwt.TokenValidationParameters.NameClaimType);
        Assert.Equal("host-role", jwt.TokenValidationParameters.RoleClaimType);
        Assert.Equal(
            ["host-audience", "host-v1-audience"],
            jwt.TokenValidationParameters.ValidAudiences);
        Assert.Equal("HostEntra", Assert.Single(schemes).Name);
    }

    [Fact]
    public async Task LeaveHostAuthenticationRegisteredAfterWorkableUntouched()
    {
        var services = new ServiceCollection();
        services.AddWorkableEntraAuthorization();
        services
            .AddAuthentication("HostCookies")
            .AddJwtBearer("HostEntra", jwt =>
            {
                jwt.Authority = "https://login.example.test/late-host/v2.0";
                jwt.Audience = "late-host-audience";
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters.ValidAudiences =
                    ["late-host-audience", "late-host-v1-audience"];
            });

        await using var provider = services.BuildServiceProvider();
        var authentication = provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        var jwt = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("HostEntra");

        Assert.Equal("HostCookies", authentication.DefaultScheme);
        Assert.Equal("https://login.example.test/late-host/v2.0", jwt.Authority);
        Assert.Equal("late-host-audience", jwt.Audience);
        Assert.False(jwt.MapInboundClaims);
        Assert.Equal(
            ["late-host-audience", "late-host-v1-audience"],
            jwt.TokenValidationParameters.ValidAudiences);
    }

    [Fact]
    public async Task LetLateHostWorkableTransportConfigurationRemainAuthoritative()
    {
        var services = new ServiceCollection();
        services.AddWorkableEntraAuthorization(options =>
            options.AuthenticationScheme = "IntegrationSelection");
        services.Configure<WorkableAspNetCoreAuthorizationOptions>(options =>
            options.TransportAuthenticationScheme = "LateHostSelection");

        await using var provider = services.BuildServiceProvider();

        Assert.Equal(
            "LateHostSelection",
            provider.GetRequiredService<IOptions<WorkableAspNetCoreAuthorizationOptions>>()
                .Value
                .TransportAuthenticationScheme);
    }

    [Fact]
    public async Task PreserveAHostConfiguredWorkableTransportSchemeWhenNoSchemeIsSelected()
    {
        var services = new ServiceCollection();
        services.AddWorkableAspNetCoreAuthorization(options =>
            options.TransportAuthenticationScheme = "HostSelectedEntra");

        services.AddWorkableEntraAuthorization();

        await using var provider = services.BuildServiceProvider();
        var workable = provider.GetRequiredService<IOptions<WorkableAspNetCoreAuthorizationOptions>>().Value;
        Assert.Equal("HostSelectedEntra", workable.TransportAuthenticationScheme);
    }

    [Fact]
    public async Task UseOneFinalOptionSetAcrossRepeatedRegistration()
    {
        var services = new ServiceCollection();
        services.AddWorkableEntraAuthorization(options =>
        {
            options.AuthenticationScheme = "FirstScheme";
            options.MapScopesToWorkableGroups = true;
            options.MapAppRolesToWorkableGroups = true;
            options.MapGroupsToWorkableGroups = true;
        });
        services.AddWorkableEntraAuthorization(options =>
        {
            options.AuthenticationScheme = "FinalScheme";
            options.MapScopesToWorkableGroups = false;
            options.MapAppRolesToWorkableGroups = false;
            options.MapGroupsToWorkableGroups = false;
        });

        await using var provider = services.BuildServiceProvider();
        var workable = provider.GetRequiredService<IOptions<WorkableAspNetCoreAuthorizationOptions>>().Value;
        Assert.Equal("FinalScheme", workable.TransportAuthenticationScheme);
        Assert.Equal(new WorkableAspNetCoreAuthorizationOptions().GroupClaimTypes, workable.GroupClaimTypes);
        Assert.Single(provider.GetServices<IWorkActorClaimsMapper>());
        Assert.Single(provider.GetServices<IWorkAuthorizationGroupClaimMapper>());
        Assert.Empty(provider.GetServices<IStartupFilter>());
    }

    [Fact]
    public async Task TreatARepeatedNoArgumentRegistrationAsEnsureOnly()
    {
        var services = new ServiceCollection();
        services.AddWorkableEntraAuthorization(options =>
        {
            options.AuthenticationScheme = "HostEntra";
            options.MapScopesToWorkableGroups = false;
            options.MapAppRolesToWorkableGroups = false;
            options.MapGroupsToWorkableGroups = false;
        });

        services.AddWorkableEntraAuthorization();

        await using var provider = services.BuildServiceProvider();
        var workable = provider.GetRequiredService<IOptions<WorkableAspNetCoreAuthorizationOptions>>().Value;
        Assert.Equal("HostEntra", workable.TransportAuthenticationScheme);
        var mapper = Assert.Single(provider.GetServices<IWorkAuthorizationGroupClaimMapper>());
        var identity = new ClaimsIdentity(
            [new Claim("oid", "entra-user")],
            authenticationType: "Test");
        foreach (var claimType in new[]
        {
            WorkableEntraAuthorizationDefaults.ScopeClaimType,
            WorkableEntraAuthorizationDefaults.RolesClaimType,
            WorkableEntraAuthorizationDefaults.GroupsClaimType,
        })
        {
            Assert.True(mapper.TryMap(identity, new Claim(claimType, "value"), out var groups));
            Assert.Empty(groups);
        }
    }

    [Fact]
    public async Task TreatARepeatedMissingConfigurationSectionAsEnsureOnly()
    {
        var services = new ServiceCollection();
        services.AddWorkableEntraAuthorization(options =>
        {
            options.AuthenticationScheme = "HostEntra";
            options.MapScopesToWorkableGroups = false;
            options.MapAppRolesToWorkableGroups = false;
            options.MapGroupsToWorkableGroups = false;
        });
        using var configuration = new ConfigurationManager();

        services.AddWorkableEntraAuthorization(
            configuration.GetSection(WorkableEntraAuthorizationDefaults.ConfigurationSectionName));

        await using var provider = services.BuildServiceProvider();
        var workable = provider.GetRequiredService<IOptions<WorkableAspNetCoreAuthorizationOptions>>().Value;
        Assert.Equal("HostEntra", workable.TransportAuthenticationScheme);
        var mapper = Assert.Single(provider.GetServices<IWorkAuthorizationGroupClaimMapper>());
        var identity = new ClaimsIdentity(
            [new Claim("oid", "entra-user")],
            authenticationType: "Test");
        foreach (var claimType in new[]
        {
            WorkableEntraAuthorizationDefaults.ScopeClaimType,
            WorkableEntraAuthorizationDefaults.RolesClaimType,
            WorkableEntraAuthorizationDefaults.GroupsClaimType,
        })
        {
            Assert.True(mapper.TryMap(identity, new Claim(claimType, "value"), out var groups));
            Assert.Empty(groups);
        }
    }

    [Fact]
    public void RejectAnEmptyExplicitAuthenticationScheme()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddWorkableEntraAuthorization(options =>
            {
                options.AuthenticationScheme = " ";
            }));

        Assert.Contains("authentication scheme", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GuardPublicRegistrationInputs()
    {
        Assert.Throws<ArgumentNullException>(() =>
            WorkableEntraServiceCollectionExtensions.AddWorkableEntraAuthorization((IServiceCollection)null!));
        Assert.Throws<ArgumentNullException>(() =>
            WorkableEntraServiceCollectionExtensions.AddWorkableEntraAuthorization(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddWorkableEntraAuthorization((Action<WorkableEntraAuthorizationOptions>)null!));
        Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddWorkableEntraAuthorization((IConfiguration)null!));
    }

    private sealed class HostJwtBearerEvents : JwtBearerEvents;
}
