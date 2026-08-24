using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Workable.Tests;

internal static class WorkableSchemeAuthenticationTestSupport
{
    public const string AmbientScheme = "Ambient";

    public const string WorkableBearerScheme = "WorkableBearer";

    public const string WorkableToken = "workable-token";

    public static IServiceCollection AddWorkableSchemeTestAuthentication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<WorkableSchemeChallengeProbe>();
        services
            .AddAuthentication(AmbientScheme)
            .AddScheme<AuthenticationSchemeOptions, WorkableSchemeAuthenticationHandler>(AmbientScheme, static _ => { })
            .AddScheme<AuthenticationSchemeOptions, WorkableSchemeAuthenticationHandler>(WorkableBearerScheme, static _ => { });
        services.Configure<WorkableAspNetCoreAuthorizationOptions>(options =>
        {
            options.TransportAuthenticationScheme = WorkableBearerScheme;
        });
        return services;
    }

    public static AuthenticationHeaderValue CreateBearerHeader()
        => new("Bearer", WorkableToken);

    private sealed class WorkableSchemeAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        WorkableSchemeChallengeProbe challengeProbe)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (string.Equals(this.Scheme.Name, AmbientScheme, StringComparison.Ordinal))
            {
                if (!challengeProbe.AuthenticateAmbient)
                {
                    return Task.FromResult(AuthenticateResult.Fail("Ambient authentication was disabled for this test."));
                }

                return Task.FromResult(AuthenticateResult.Success(CreateTicket(
                    this.Scheme.Name,
                    "ambient-user-1",
                    "Ambient User",
                    "ambient.user@example.test",
                    challengeProbe.AmbientAuthenticationExpiresUtc)));
            }

            if (string.Equals(this.Scheme.Name, WorkableBearerScheme, StringComparison.Ordinal))
            {
                var token = ReadBearerToken(this.Request);
                if (string.Equals(token, WorkableToken, StringComparison.Ordinal))
                {
                    return Task.FromResult(AuthenticateResult.Success(CreateTicket(
                        this.Scheme.Name,
                        "workable-user-1",
                        "Workable Bearer User",
                        "workable.user@example.test",
                        challengeProbe.WorkableAuthenticationExpiresUtc)));
                }

                return Task.FromResult(AuthenticateResult.Fail("Bearer token was not provided."));
            }

            return Task.FromResult(AuthenticateResult.NoResult());
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            challengeProbe.Record(this.Scheme.Name);
            this.Response.StatusCode = challengeProbe.StatusCode;
            this.Response.Headers[WorkableSchemeChallengeProbe.HeaderName] = this.Scheme.Name;
            if (challengeProbe.Location is not null)
            {
                this.Response.Headers.Location = challengeProbe.Location;
            }
            return Task.CompletedTask;
        }

        private static AuthenticationTicket CreateTicket(
            string authenticationType,
            string id,
            string name,
            string email,
            DateTimeOffset? expiresUtc)
            => new(
                new ClaimsPrincipal(new ClaimsIdentity(
                    CreateClaims(id, name, email),
                    authenticationType)),
                new AuthenticationProperties { ExpiresUtc = expiresUtc },
                authenticationType);

        private static IEnumerable<Claim> CreateClaims(
            string id,
            string name,
            string email)
        {
            yield return new Claim(ClaimTypes.NameIdentifier, id);
            yield return new Claim(ClaimTypes.Name, name);
            yield return new Claim(ClaimTypes.Email, email);
            yield return new Claim(WorkableEntraAuthorizationDefaults.ScopeClaimType, "work.read work.execute");

            foreach (var group in DefaultGroups())
            {
                yield return new Claim("groups", group);
            }
        }

        private static string? ReadBearerToken(Microsoft.AspNetCore.Http.HttpRequest request)
        {
            var authorization = request.Headers.Authorization.ToString();
            if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return authorization["Bearer ".Length..].Trim();
            }

            return null;
        }

        private static IEnumerable<string> DefaultGroups()
            => TransportAuthorizationTestSupport.ReadGroups
                .Concat(TransportAuthorizationTestSupport.OperateGroups)
                .Concat(TransportAuthorizationTestSupport.DiagnosticsGroups)
                .Concat(TransportAuthorizationTestSupport.ControlSystemGroups)
                .Concat(TransportAuthorizationTestSupport.ReadAllWorkGroups)
                .Concat(TransportAuthorizationTestSupport.OperateAllWorkGroups)
                .Concat(TransportAuthorizationTestSupport.SystemAdministratorGroups)
                .Concat(TransportAuthorizationTestSupport.WorkAdministratorGroups);
    }
}

internal sealed class WorkableSchemeChallengeProbe
{
    public const string HeaderName = "X-Test-Authentication-Challenge";

    private int count;

    public int Count => Volatile.Read(ref this.count);

    public string? LastScheme { get; private set; }

    public bool AuthenticateAmbient { get; set; } = true;

    public int StatusCode { get; set; } = StatusCodes.Status401Unauthorized;

    public string? Location { get; set; }

    public DateTimeOffset? AmbientAuthenticationExpiresUtc { get; set; }

    public DateTimeOffset? WorkableAuthenticationExpiresUtc { get; set; }

    public void Record(string scheme)
    {
        this.LastScheme = scheme;
        Interlocked.Increment(ref this.count);
    }
}
