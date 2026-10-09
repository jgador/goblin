using System;
using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Http;

namespace Goblin.Web;

// Shared trust boundary for authenticated proxies with a configured upstream.
// Each proxy still owns its product-specific route allowlist and content policy.
internal static class FixedProxyBoundary
{
    internal static string Destination(string value, string error)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException(error);
        return uri.AbsoluteUri;
    }

    internal static HttpMessageInvoker CreateClient() => new(new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10)
    });

    internal static bool SafePath(string path)
    {
        if (path.Contains('%') || path.Contains('\\') || path.Contains("//", StringComparison.Ordinal)) return false;
        foreach (string segment in path.Split('/')) if (segment is "." or "..") return false;
        return true;
    }

    internal static void ProtectResponse(HttpResponse response, string contentSecurityPolicy)
    {
        response.Headers.Remove("Set-Cookie");
        response.Headers.Remove("Server");
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.XFrameOptions = "DENY";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.ContentSecurityPolicy = contentSecurityPolicy;
    }
}
