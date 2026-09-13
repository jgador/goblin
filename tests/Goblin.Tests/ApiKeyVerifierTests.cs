using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Web;
using Xunit;

namespace Goblin.Tests;

public sealed class ApiKeyVerifierTests
{
    private const string Key = "sk-test-ONLY-A-FAKE-KEY-1234567890";

    [Theory]
    [InlineData(200, "accepted")]
    [InlineData(403, "unverified")]
    [InlineData(429, "unverified")]
    [InlineData(401, "invalid_api_key")]
    [InlineData(503, "verification_unavailable")]
    [InlineData(302, "verification_unavailable")]
    public async Task ChecksModelsEndpointAndClassifiesResponses(int status, string expected)
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal("https://api.openai.com/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"Bearer {Key}", request.Headers.Authorization!.ToString());
            Assert.Null(request.Content);
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("PRIVATE UPSTREAM BODY") };
        });
        using var client = new HttpClient(handler);
        var verifier = new ApiKeyVerifier(client);
        if (status is 200 or 403 or 429) Assert.Equal(expected, await verifier.VerifyAsync(Key));
        else
        {
            PublicError error = await Assert.ThrowsAsync<PublicError>(() => verifier.VerifyAsync(Key));
            Assert.Equal(expected, error.Code);
            Assert.DoesNotContain("PRIVATE", error.Message);
        }
    }

    [Fact]
    public async Task NetworkErrorsNeverExposeCredentials()
    {
        using var client = new HttpClient(new Handler(_ => throw new HttpRequestException(Key)));
        PublicError error = await Assert.ThrowsAsync<PublicError>(() => new ApiKeyVerifier(client).VerifyAsync(Key));
        Assert.Equal("verification_unavailable", error.Code);
        Assert.DoesNotContain(Key, error.Message);
    }

    [Theory]
    [InlineData("http://public.example.test")]
    [InlineData("https://user:password@example.test")]
    [InlineData("https://example.test/path")]
    [InlineData("https://example.test?query=secret")]
    public void RejectsInvalidPublicOrigins(string origin) => Assert.Throws<ArgumentException>(() => Workspace.ValidateOrigin(origin));

    [Fact]
    public void PublicHttpRequiresExplicitOptIn()
    {
        const string origin = "http://custom-name.southeastasia.cloudapp.azure.com";
        Assert.Throws<ArgumentException>(() => Workspace.ValidateOrigin(origin));
        Assert.Equal(origin + "/", Workspace.ValidateOrigin(origin, allowInsecureHttp: true).AbsoluteUri);
    }

    [Theory]
    [InlineData("ftp://example.test")]
    [InlineData("http://user:password@example.test")]
    [InlineData("http://example.test/path")]
    [InlineData("http://example.test?query=secret")]
    [InlineData("http://example.test#fragment")]
    public void HttpOptInStillRequiresAnExactOrigin(string origin) =>
        Assert.Throws<ArgumentException>(() => Workspace.ValidateOrigin(origin, allowInsecureHttp: true));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
