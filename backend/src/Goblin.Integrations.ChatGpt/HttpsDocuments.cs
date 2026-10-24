using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Goblin.Integrations.ChatGpt;

// Endpoints are derived from trusted configuration, never from bearer tokens.
// Both discovery and keys are bounded and fetched without cookies or redirects.
public sealed class HttpsDocuments : IJwksDocumentSource, IDisposable
{
    private readonly ChatGptConfiguration _configuration;
    private readonly HttpClient _http;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public HttpsDocuments(ChatGptConfiguration configuration) : this(configuration, new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        MaxResponseHeadersLength = 8,
        MaxConnectionsPerServer = 2,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1)
    })
    { }

    internal HttpsDocuments(ChatGptConfiguration configuration, HttpMessageHandler handler)
    {
        _configuration = configuration;
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<OAuthDiscoverySummary> VerifyDiscoveryAsync(CancellationToken token)
    {
        string json = await ReadDocumentAsync(_configuration.Discovery, 16384, token);
        OAuthDiscoverySummary metadata = new OAuthDiscovery(_configuration.Issuer).Review(json);
        if (metadata.AuthorizationEndpoint != _configuration.Provider.AuthorizationEndpoint ||
            metadata.TokenEndpoint != _configuration.Provider.TokenEndpoint ||
            metadata.JwksEndpoint != _configuration.Provider.JwksEndpoint)
            throw new ChatGptAuthenticationFailure();
        return metadata;
    }

    public async Task<string> ReadAsync(Uri pinnedAddress, CancellationToken token)
    {
        if (pinnedAddress != _configuration.Provider.JwksEndpoint) throw new ChatGptAuthenticationFailure();
        await VerifyDiscoveryAsync(token);
        return await ReadDocumentAsync(pinnedAddress, 32768, token);
    }

    private async Task<string> ReadDocumentAsync(Uri address, int maximumBytes, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/jwk-set+json"));
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            string? media = response.Content.Headers.ContentType?.MediaType;
            string? charset = response.Content.Headers.ContentType?.CharSet;
            if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri != address ||
                response.Content.Headers.ContentLength > maximumBytes || response.Content.Headers.ContentEncoding.Count != 0 ||
                media is not ("application/json" or "application/jwk-set+json") ||
                charset is not null && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                throw new ChatGptAuthenticationFailure();
            await using Stream stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            byte[] buffer = new byte[maximumBytes + 1];
            int count = 0;
            while (count <= maximumBytes)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(count), deadline.Token);
                if (read == 0) break;
                count += read;
            }
            if (count == 0 || count > maximumBytes) throw new ChatGptAuthenticationFailure();
            return StrictUtf8.GetString(buffer, 0, count);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new ChatGptAuthenticationFailure(); }
    }

    public void Dispose() => _http.Dispose();
}
