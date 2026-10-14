using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Goblin.Mcp.Prototype;

// Library-only source. Construction never fetches; no production registration.
// Owns a dedicated client, without ambient cookies, credentials or proxy use.
public sealed class CustomerHttpsJwksDocumentSource : ICustomerJwksDocumentSource, IDisposable
{
    private const int MaximumBytes = 32768;
    private readonly Uri _address;
    private readonly HttpClient _http;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public CustomerHttpsJwksDocumentSource(OAuthDiscoverySummary configuration)
        : this(configuration, CreateHandler(configuration)) { }

    // Only the friend test assembly can replace transport. Never accept a shared
    // credentialed HttpClient or arbitrary handler from application composition.
    internal CustomerHttpsJwksDocumentSource(OAuthDiscoverySummary configuration, HttpMessageHandler handler)
    {
        _address = Validate(configuration);
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static SocketsHttpHandler CreateHandler(OAuthDiscoverySummary configuration)
    {
        _ = Validate(configuration);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxResponseHeadersLength = 8,
            MaxConnectionsPerServer = 2,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1)
            // Keep system TLS chain/hostname validation. No callback bypass.
        };
    }

    public async Task<string> ReadAsync(Uri pinnedAddress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (pinnedAddress?.OriginalString != _address.OriginalString) throw new ConnectorAuthenticationFailure();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _address);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/jwk-set+json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK ||
                response.RequestMessage?.RequestUri?.OriginalString != _address.OriginalString ||
                response.Content.Headers.ContentLength > MaximumBytes || response.Content.Headers.ContentEncoding.Count != 0)
                throw new ConnectorAuthenticationFailure();
            MediaTypeHeaderValue? contentType = response.Content.Headers.ContentType;
            if (contentType?.MediaType is not string media ||
                (!media.Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
                 !media.Equals("application/jwk-set+json", StringComparison.OrdinalIgnoreCase)) ||
                (contentType.CharSet is string charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
                throw new ConnectorAuthenticationFailure();
            await using Stream stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            byte[] buffer = new byte[MaximumBytes + 1];
            int total = 0;
            while (true)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), deadline.Token);
                if (count == 0) break;
                total += count;
                if (total > MaximumBytes) throw new ConnectorAuthenticationFailure();
            }
            if (total == 0) throw new ConnectorAuthenticationFailure();
            return StrictUtf8.GetString(buffer, 0, total);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new ConnectorAuthenticationFailure(); }
    }

    public void Dispose() => _http.Dispose();

    private static Uri Validate(OAuthDiscoverySummary configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Uri address = configuration.JwksEndpoint;
        if (!address.IsAbsoluteUri || address.Scheme != "https" || address.UserInfo.Length != 0 ||
            address.Query.Length != 0 || address.Fragment.Length != 0 || address.OriginalString.Contains('\\') ||
            address.OriginalString.Any(char.IsControl) || address.OriginalString.Length > 2048 ||
            !Uri.TryCreate(configuration.Issuer, UriKind.Absolute, out Uri? issuer) || issuer.Scheme != "https" ||
            address.GetLeftPart(UriPartial.Authority) != issuer.GetLeftPart(UriPartial.Authority))
            throw new ArgumentException("Configure a pinned HTTPS customer signing-key endpoint.");
        return address;
    }
}
