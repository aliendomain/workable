using Microsoft.AspNetCore.Http;

namespace Workable;

internal static class WorkableSignalRAccessToken
{
    public static bool TryPromote(HttpContext context, WorkableSignalROptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.PromoteAccessTokensFromQueryString ||
            context.Request.Headers.ContainsKey("Authorization") ||
            !context.Request.Query.TryGetValue(options.AccessTokenQueryStringName, out var values) ||
            values.Count != 1)
        {
            return false;
        }

        var candidate = values.FirstOrDefault();
        if (!IsBearerToken(candidate))
        {
            return false;
        }

        context.Request.Headers.Authorization = $"Bearer {candidate}";
        return true;
    }

    private static bool IsBearerToken(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var hasTokenCharacter = false;
        var paddingStarted = false;
        foreach (var character in value)
        {
            if (character == '=')
            {
                paddingStarted = true;
                continue;
            }

            if (paddingStarted ||
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~' or '+' or '/'))
            {
                return false;
            }

            hasTokenCharacter = true;
        }

        return hasTokenCharacter;
    }
}
