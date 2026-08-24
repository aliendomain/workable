using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Workable.Tests;

internal static class EntraJwtTestSupport
{
    public const string TenantId = "11111111-2222-3333-4444-555555555555";
    public const string Audience = "api://aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    public const string ActorObjectId = "99999999-8888-7777-6666-555555555555";
    public const string ActorSubjectId = "pairwise-subject-id";
    public const string MicrosoftObjectIdClaimType =
        "http://schemas.microsoft.com/identity/claims/objectidentifier";
    public const string MicrosoftScopeClaimType =
        "http://schemas.microsoft.com/identity/claims/scope";
    public const string Issuer = $"https://login.microsoftonline.com/{TenantId}/v2.0";

    private static readonly SymmetricSecurityKey SigningKey = new(
        Encoding.UTF8.GetBytes("workable-entra-tests-signing-key-at-least-256-bits"))
    {
        KeyId = "workable-entra-tests",
    };

    public static void ConfigureValidation(JwtBearerOptions options)
    {
        options.Audience = Audience;
        var configuration = new OpenIdConnectConfiguration
        {
            Issuer = Issuer,
        };
        configuration.SigningKeys.Add(SigningKey);
        options.ConfigurationManager =
            new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
    }

    public static string CreateToken(params Claim[] claims)
        => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            IssuedAt = DateTime.UtcNow,
            Issuer = Issuer,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(claims),
        });
}
