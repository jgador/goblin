using System;
using Goblin.Web;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Goblin.Tests;

public sealed class FixedProxyBoundaryTests
{
    [Theory]
    [InlineData("http://headlamp", "http://headlamp/")]
    [InlineData("https://logs.internal:9443/", "https://logs.internal:9443/")]
    public void DestinationAcceptsOnlyAnHttpOrigin(string value, string expected) =>
        Assert.Equal(expected, FixedProxyBoundary.Destination(value, "invalid"));

    [Theory]
    [InlineData("headlamp")]
    [InlineData("ftp://headlamp/")]
    [InlineData("https://user@headlamp/")]
    [InlineData("https://headlamp/path")]
    [InlineData("https://headlamp/?query=true")]
    [InlineData("https://headlamp/#fragment")]
    public void DestinationRejectsAnythingBeyondAFixedHttpOrigin(string value) =>
        Assert.Equal("invalid", Assert.Throws<ArgumentException>(() => FixedProxyBoundary.Destination(value, "invalid")).Message);

    [Theory]
    [InlineData("/headlamp/static/app.js")]
    [InlineData("/logs/select/logsql/query")]
    public void OrdinaryProxyPathsAreSafe(string path) => Assert.True(FixedProxyBoundary.SafePath(path));

    [Theory]
    [InlineData("/headlamp/%2e%2e/secret")]
    [InlineData("/headlamp/../secret")]
    [InlineData("/logs/./query")]
    [InlineData("/logs//query")]
    [InlineData("/logs\\query")]
    public void AmbiguousProxyPathsAreRejected(string path) => Assert.False(FixedProxyBoundary.SafePath(path));

    [Fact]
    public void ResponseProtectionRemovesUpstreamIdentityAndAppliesBrowserPolicy()
    {
        var context = new DefaultHttpContext();
        context.Response.Headers.SetCookie = "secret=value";
        context.Response.Headers.Server = "upstream";

        FixedProxyBoundary.ProtectResponse(context.Response, "default-src 'self'");

        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.False(context.Response.Headers.ContainsKey("Server"));
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions);
        Assert.Equal("DENY", context.Response.Headers.XFrameOptions);
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"]);
        Assert.Equal("default-src 'self'", context.Response.Headers.ContentSecurityPolicy);
    }
}
