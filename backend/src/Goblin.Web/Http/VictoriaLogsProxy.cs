using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Forwarder;

namespace Goblin.Web;

// VictoriaLogs serves both ingestion and queries. Only the UI and explicit read
// APIs cross this authenticated boundary, including reads that use POST bodies.
public sealed class VictoriaLogsProxy : IDisposable
{
    private readonly IHttpForwarder _forwarder;
    private readonly string _destination;
    private readonly HttpMessageInvoker _client;
    private readonly WorkspacePreferences _preferences;
    private static readonly ForwarderRequestConfig RequestConfig = new() { ActivityTimeout = TimeSpan.FromMinutes(2) };
    private static readonly LogsTransformer Transformer = new();

    public VictoriaLogsProxy(IHttpForwarder forwarder, string destination, WorkspacePreferences preferences)
    {
        if (!Uri.TryCreate(destination, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("GOBLIN_VICTORIALOGS_URL must be the internal VictoriaLogs HTTP origin (configured with -http.pathPrefix=/logs).");
        _forwarder = forwarder;
        _preferences = preferences;
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
            throw new PublicError("logs_read_only", "This log view allows searching and inspection only.", 403);
        if (IsDocument(path) && (await _preferences.ReadAsync(context.RequestAborted)).TimeZone is null)
        { context.Response.Redirect("/?returnTo=logs"); return; }
        if (path is "/logs" or "/logs/") { context.Response.Redirect("/logs/select/vmui/"); return; }
        IHttpMaxRequestBodySizeFeature? bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = 65_536;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        // Live tail reconnects must periodically pass Goblin authentication again.
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        ForwarderError error = await _forwarder.SendAsync(context, _destination, _client, RequestConfig, Transformer, deadline.Token);
        if (error != ForwarderError.None && !context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
            throw new PublicError("logs_unavailable", "The log view is temporarily unavailable. Try again shortly.", 503);
    }

    private static bool Allowed(string path, bool read, bool post)
    {
        if (path.Contains('%') || path.Contains('\\') || path.Contains("//", StringComparison.Ordinal)) return false;
        foreach (string segment in path.Split('/')) if (segment is "." or "..") return false;
        if (read && (path is "/logs" or "/logs/" or "/logs/select/vmui" or "/logs/select/buildinfo" ||
                     path.StartsWith("/logs/select/vmui/", StringComparison.Ordinal))) return true;
        return (read || post) && path is
            "/logs/select/logsql/query" or "/logs/select/logsql/tail" or
            "/logs/select/logsql/hits" or "/logs/select/logsql/facets" or
            "/logs/select/logsql/query_time_range" or
            "/logs/select/logsql/field_names" or "/logs/select/logsql/field_values" or
            "/logs/select/logsql/stream_field_names" or "/logs/select/logsql/stream_field_values" or
            "/logs/select/logsql/stream_ids" or "/logs/select/logsql/streams" or
            "/logs/select/logsql/stats_query" or "/logs/select/logsql/stats_query_range";
    }

    public void Dispose() => _client.Dispose();

    private static bool IsDocument(string path) => path is "/logs" or "/logs/" or "/logs/select/vmui" or
        "/logs/select/vmui/" or "/logs/select/vmui/index.html";

    private sealed class LogsTransformer : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage proxyRequest,
            string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(context, proxyRequest, destinationPrefix, cancellationToken);
            // Goblin cookies, upstream credentials, tenant selectors and arbitrary
            // proxy headers must never reach VictoriaLogs.
            proxyRequest.Headers.Clear();
            foreach (string header in new[] { "Accept", "Accept-Encoding", "Accept-Language", "Range", "If-Range",
                         "If-None-Match", "If-Modified-Since", "User-Agent" })
                if (context.Request.Headers.TryGetValue(header, out StringValues value))
                    proxyRequest.Headers.TryAddWithoutValidation(header, value.ToArray());
            if (IsDocument(context.Request.Path.Value!)) proxyRequest.Headers.Remove("Accept-Encoding");
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
            // The pinned VMUI uses external scripts and inline layout styles.
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data:; font-src 'self' data:; connect-src 'self'; worker-src 'self' blob:; " +
                "object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            if (HttpMethods.IsGet(context.Request.Method) && IsDocument(context.Request.Path.Value!) &&
                proxyResponse?.StatusCode == HttpStatusCode.OK && proxyResponse.Content.Headers.ContentType?.MediaType == "text/html")
            {
                await proxyResponse.Content.LoadIntoBufferAsync(1_048_576, cancellationToken);
                string html = await proxyResponse.Content.ReadAsStringAsync(cancellationToken);
                html = html.Replace("<head>", "<head><script src=\"/api/preferences/logs.js\"></script>", StringComparison.OrdinalIgnoreCase);
                context.Response.Headers.Remove("ETag");
                context.Response.Headers.Remove("Content-Encoding");
                context.Response.ContentLength = Encoding.UTF8.GetByteCount(html);
                await context.Response.WriteAsync(html, cancellationToken);
                return false;
            }
            return copy;
        }
    }
}
