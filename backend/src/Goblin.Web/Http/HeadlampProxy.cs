using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Forwarder;

namespace Goblin.Web;

// A fixed upstream, authenticated by Goblin. Kubernetes independently enforces
// the dedicated viewer account's permissions; its token never enters the browser.
public sealed class HeadlampProxy : IDisposable
{
    private readonly IHttpForwarder _forwarder;
    private readonly string _destination;
    private readonly HttpMessageInvoker _client;
    private static readonly ForwarderRequestConfig RequestConfig = new() { ActivityTimeout = TimeSpan.FromMinutes(2) };
    private static readonly HeadlampTransformer Transformer = new();

    public HeadlampProxy(IHttpForwarder forwarder, string destination)
    {
        if (!Uri.TryCreate(destination, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("GOBLIN_HEADLAMP_URL must be the internal Headlamp HTTP origin.");
        _forwarder = forwarder;
        _destination = uri.AbsoluteUri;
        _client = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        });
    }

    public async Task SendAsync(HttpContext context, Workspace workspace)
    {
        HttpRequest request = context.Request;
        string path = request.Path.Value!;
        bool read = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
        if (!read || request.Headers.ContainsKey("Origin")) workspace.ValidateOrigin(request);
        if (!Allowed(path, read, HttpMethods.IsPost(request.Method)))
            throw new PublicError("cluster_read_only", "This cluster view allows inspection only.", 403);
        if (path == "/headlamp") { context.Response.Redirect("/headlamp/"); return; }
        IHttpMaxRequestBodySizeFeature? bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = 65_536;
        // Bound watch connections so authorization is periodically checked again.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        ForwarderError error = await _forwarder.SendAsync(context, _destination, _client, RequestConfig, Transformer, deadline.Token);
        if (error != ForwarderError.None && !context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
            throw new PublicError("cluster_unavailable", "The cluster view is temporarily unavailable. Try again shortly.", 503);
    }

    private static bool Allowed(string path, bool read, bool post)
    {
        // Keep escaped separators and dot segments from changing the allowed upstream route.
        if (path.Contains('%') || path.Contains('\\') || path.Contains("//", StringComparison.Ordinal)) return false;
        foreach (string segment in path.Split('/')) if (segment is "." or "..") return false;
        if (post) return path is
            "/headlamp/clusters/goblin/apis/authorization.k8s.io/v1/selfsubjectaccessreviews" or
            "/headlamp/clusters/goblin/apis/authorization.k8s.io/v1/selfsubjectrulesreviews" or
            "/headlamp/clusters/goblin/apis/authentication.k8s.io/v1/selfsubjectreviews";
        if (!read) return false;
        if (path.StartsWith("/headlamp/clusters/", StringComparison.Ordinal))
            return path is "/headlamp/clusters/goblin/api" or "/headlamp/clusters/goblin/apis" or
                       "/headlamp/clusters/goblin/version" or "/headlamp/clusters/goblin/me" ||
                   path.StartsWith("/headlamp/clusters/goblin/api/", StringComparison.Ordinal) ||
                   path.StartsWith("/headlamp/clusters/goblin/apis/", StringComparison.Ordinal);
        // Only the UI, its static assets/config, and Kubernetes discovery/read APIs
        // are exposed. Headlamp's external proxy, kubeconfig and plugin installers
        // are deliberately outside this route set.
        return path is "/headlamp" or "/headlamp/" or "/headlamp/config" or "/headlamp/plugins" or "/headlamp/settings" ||
               path.StartsWith("/headlamp/settings/", StringComparison.Ordinal) ||
               path.StartsWith("/headlamp/c/", StringComparison.Ordinal) ||
               path.StartsWith("/headlamp/static/", StringComparison.Ordinal) ||
               path.StartsWith("/headlamp/assets/", StringComparison.Ordinal) ||
               path.StartsWith("/headlamp/locales/", StringComparison.Ordinal) ||
               path.StartsWith("/headlamp/plugins/", StringComparison.Ordinal) ||
               path.StartsWith("/headlamp/static-plugins/", StringComparison.Ordinal) ||
               (path.LastIndexOf('/') == "/headlamp".Length && System.IO.Path.HasExtension(path));
    }

    public void Dispose() => _client.Dispose();

    private sealed class HeadlampTransformer : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage proxyRequest,
            string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(context, proxyRequest, destinationPrefix, cancellationToken);
            // Do not forward Goblin cookies, supplied Kubernetes credentials,
            // impersonation headers, or arbitrary proxy/identity headers.
            proxyRequest.Headers.Clear();
            foreach (string header in new[] { "Accept", "Accept-Encoding", "Accept-Language", "Range", "If-Range",
                         "If-None-Match", "If-Modified-Since", "User-Agent",
                         "Sec-WebSocket-Key", "Sec-WebSocket-Version", "Sec-WebSocket-Protocol" })
                if (context.Request.Headers.TryGetValue(header, out StringValues value))
                    proxyRequest.Headers.TryAddWithoutValidation(header, value.ToArray());
        }

        public override async ValueTask<bool> TransformResponseAsync(HttpContext context, HttpResponseMessage? proxyResponse,
            CancellationToken cancellationToken)
        {
            bool copy = await base.TransformResponseAsync(context, proxyResponse, cancellationToken);
            context.Response.Headers.Remove("Set-Cookie");
            context.Response.Headers.Remove("Server");
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            // Headlamp's two inline bootstrap scripts are pinned with its image.
            // Their hashes are verified against the real UI when upgrading Headlamp.
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self' 'sha256-IGC9pIbAmiVjV5wzaAgJ2CWGUph0A9ngnNPySh6ZP/U=' " +
                "'sha256-a/I3uOpd1JHbda9Yul+v1pGVdTvTpXn8D84n1wc++Iw='; style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data:; font-src 'self' data:; connect-src 'self'; worker-src 'self' blob:; " +
                "object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            return copy;
        }
    }
}
