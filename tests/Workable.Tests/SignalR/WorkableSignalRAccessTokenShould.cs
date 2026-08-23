using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class WorkableSignalRAccessTokenShould
{
    [Fact]
    public void PromoteAValidBearerToken()
    {
        var context = CreateContext("abc-._~+/==");

        var promoted = WorkableSignalRAccessToken.TryPromote(context, new WorkableSignalROptions());

        Assert.True(promoted);
        Assert.Equal("Bearer abc-._~+/==", context.Request.Headers.Authorization);
    }

    [Theory]
    [InlineData("")]
    [InlineData("token with spaces")]
    [InlineData("token\r\nwith-control-characters")]
    [InlineData("padding=before-text")]
    [InlineData("=")]
    [InlineData("token:with-invalid-character")]
    [InlineData("tøken")]
    public void RejectMalformedBearerTokens(string accessToken)
    {
        var context = CreateContext(accessToken);

        var promoted = WorkableSignalRAccessToken.TryPromote(context, new WorkableSignalROptions());

        Assert.False(promoted);
        Assert.False(context.Request.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public void LeaveAnExistingAuthorizationHeaderUnchanged()
    {
        var context = CreateContext("query-token");
        context.Request.Headers.Authorization = "Basic host-credentials";

        var promoted = WorkableSignalRAccessToken.TryPromote(context, new WorkableSignalROptions());

        Assert.False(promoted);
        Assert.Equal("Basic host-credentials", context.Request.Headers.Authorization);
    }

    [Fact]
    public void LeaveAWhitespaceAuthorizationHeaderForTheHostToReject()
    {
        var context = CreateContext("query-token");
        context.Request.Headers.Authorization = " ";

        var promoted = WorkableSignalRAccessToken.TryPromote(context, new WorkableSignalROptions());

        Assert.False(promoted);
        Assert.Equal(" ", context.Request.Headers.Authorization);
    }

    [Fact]
    public void IgnoreQueryTokensWhenPromotionIsDisabled()
    {
        var context = CreateContext("query-token");

        var promoted = WorkableSignalRAccessToken.TryPromote(
            context,
            new WorkableSignalROptions { PromoteAccessTokensFromQueryString = false });

        Assert.False(promoted);
        Assert.False(context.Request.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public void UseTheConfiguredQueryKey()
    {
        var context = new DefaultHttpContext();
        context.Request.Query = new QueryCollection(
            new Dictionary<string, StringValues> { ["workable_token"] = "query-token" });

        var promoted = WorkableSignalRAccessToken.TryPromote(
            context,
            new WorkableSignalROptions { AccessTokenQueryStringName = "workable_token" });

        Assert.True(promoted);
        Assert.Equal("Bearer query-token", context.Request.Headers.Authorization);
    }

    [Fact]
    public void RejectDuplicateQueryTokenValues()
    {
        var context = new DefaultHttpContext();
        context.Request.Query = new QueryCollection(
            new Dictionary<string, StringValues>
            {
                ["access_token"] = new StringValues(["first-token", "second-token"]),
            });

        var promoted = WorkableSignalRAccessToken.TryPromote(context, new WorkableSignalROptions());

        Assert.False(promoted);
        Assert.False(context.Request.Headers.ContainsKey("Authorization"));
    }

    private static DefaultHttpContext CreateContext(string accessToken)
    {
        var context = new DefaultHttpContext();
        context.Request.Query = new QueryCollection(
            new Dictionary<string, StringValues> { ["access_token"] = accessToken });
        return context;
    }
}
