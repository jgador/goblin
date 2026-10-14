using System.ComponentModel;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Goblin.Mcp.Prototype;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Goblin.Mcp.Http.Prototype;

// Composition only: no executable/listener and no default grants or data source.
public static class PrivateMcpHttpAdapter
{
    internal static readonly object IdentityKey = new();
    public const string MetadataPath = "/.well-known/oauth-protected-resource/mcp";

    public static void AddServices(IServiceCollection services, ConnectorReadBoundary boundary) =>
        AddServices<ConnectorReadBoundary, PrivateWorkTools>(services, boundary, boundary.Resource);

    public static void AddServices(IServiceCollection services, RotatingConnectorReadBoundary boundary) =>
        AddServices<RotatingConnectorReadBoundary, RotatingPrivateWorkTools>(services, boundary, boundary.Resource);

    private static void AddServices<TBoundary, TTools>(IServiceCollection services, TBoundary boundary, string resourceId)
        where TBoundary : class where TTools : class
    {
        Uri resource = new(resourceId);
        if (resource.AbsolutePath != "/mcp")
            throw new ArgumentException("The private prototype requires the exact /mcp resource path.");
        services.AddSingleton(boundary);
        services.AddHttpContextAccessor();
        services.AddMcpServer().WithHttpTransport(options =>
        {
            options.Stateless = true;
        }).WithTools<TTools>();
    }

    public static void Map(WebApplication app, ConnectorReadBoundary boundary) =>
        Map(app, boundary.Issuer, boundary.Resource, boundary.AuthenticateAsync);

    public static void Map(WebApplication app, RotatingConnectorReadBoundary boundary) =>
        Map(app, boundary.Issuer, boundary.Resource, boundary.AuthenticateAsync);

    private static void Map<TIdentity>(WebApplication app, string issuer, string resourceId,
        Func<string, CancellationToken, Task<TIdentity>> authenticate) where TIdentity : class
    {
        Uri resource = new(resourceId);
        string origin = resource.GetLeftPart(UriPartial.Authority);
        string metadataUrl = origin + MetadataPath;
        string challenge = $"Bearer resource_metadata=\"{metadataUrl}\", scope=\"{ConnectorReadBoundary.ReadScope}\"";
        app.Use(async (context, next) =>
        {
            bool metadata = context.Request.Path == MetadataPath;
            bool mcp = context.Request.Path.StartsWithSegments("/mcp");
            if (!metadata && !mcp) { await next(context); return; }
            context.Response.Headers.CacheControl = "no-store";
            if (!string.Equals(context.Request.Host.Value, resource.Authority, StringComparison.OrdinalIgnoreCase) ||
                context.Request.Headers.TryGetValue("Origin", out var origins) &&
                (origins.Count != 1 || !string.Equals(origins[0], origin, StringComparison.Ordinal)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new SafeFailure("Request is not allowed."));
                return;
            }
            if (metadata) { await next(context); return; }
            string credential = context.Request.Headers.Authorization.ToString();
            if (context.Request.Headers.Authorization.Count != 1 ||
                !AuthenticationHeaderValue.TryParse(credential, out var header) ||
                !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(header.Parameter))
            {
                await UnauthorizedAsync(context, challenge);
                return;
            }
            try
            {
                context.Items[IdentityKey] = await authenticate(header.Parameter, context.RequestAborted);
            }
            catch (ConnectorAuthenticationFailure)
            {
                await UnauthorizedAsync(context, challenge);
                return;
            }
            await next(context);
        });
        app.MapGet(MetadataPath, () => new ProtectedResourceMetadata(resourceId, [issuer], [ConnectorReadBoundary.ReadScope]));
        app.MapMcp("/mcp");
    }

    private static async Task UnauthorizedAsync(HttpContext context, string challenge)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = challenge;
        await context.Response.WriteAsJsonAsync(new SafeFailure("Connector authentication is required."));
    }

    private sealed record SafeFailure(string Error);

    private sealed record ProtectedResourceMetadata(
        [property: JsonPropertyName("resource")] string Resource,
        [property: JsonPropertyName("authorization_servers")] string[] AuthorizationServers,
        [property: JsonPropertyName("scopes_supported")] string[] ScopesSupported);
}

[McpServerToolType]
public sealed class PrivateWorkTools
{
    private readonly IHttpContextAccessor _context;
    private readonly ConnectorReadBoundary _boundary;

    public PrivateWorkTools(IHttpContextAccessor context, ConnectorReadBoundary boundary)
    {
        _context = context;
        _boundary = boundary;
    }

    [McpServerTool(Name = "goblin_get_work_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read status for a Goblin Work ID authorized by this installation. Returns no history or credentials.")]
    public async Task<WorkStatusResult> GetStatusAsync(string workId, CancellationToken cancellationToken)
    {
        if (!long.TryParse(workId, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0 ||
            _context.HttpContext?.Items[PrivateMcpHttpAdapter.IdentityKey] is not ConnectorIdentity identity)
            throw new McpException("Connector Work read access is not authorized.");
        try { return await _boundary.ReadAsync(identity, id, cancellationToken); }
        catch (WorkReadFailure) { throw new McpException("Connector Work read access is not authorized."); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { throw new McpException("Work status is unavailable."); }
    }
}
