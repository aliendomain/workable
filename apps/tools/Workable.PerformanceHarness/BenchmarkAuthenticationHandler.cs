using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Workable.PerformanceHarness;

internal sealed class BenchmarkAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "WorkableBenchmark";

    internal static ClaimsPrincipal CreatePrincipal()
    {
        Claim[] claims =
        [
            new Claim(ClaimTypes.NameIdentifier, "workable.performance.transport"),
            new Claim(ClaimTypes.Name, "Workable Performance Transport"),
            new Claim("groups", WorkableBenchmarkSystem.OperatorGroup),
        ];
        var identity = new ClaimsIdentity(claims, SchemeName);
        return new ClaimsPrincipal(identity);
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var ticket = new AuthenticationTicket(CreatePrincipal(), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
