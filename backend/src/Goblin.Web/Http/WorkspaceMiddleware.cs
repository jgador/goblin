using System;
using System.Threading.Tasks;
using Goblin.Application.GitRepositories;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Goblin.Web;

internal sealed class WorkspaceMiddleware
{
    public const string SessionKey = "goblin.session";
    private readonly RequestDelegate _next;
    private readonly Workspace _workspace;
    private readonly StaticAssets _assets;
    private readonly bool _gitRepositoryListener;
    private readonly HeadlampProxy? _headlamp;
    private readonly VictoriaLogsProxy? _logs;

    public WorkspaceMiddleware(RequestDelegate next, Workspace workspace, StaticAssets assets, bool gitRepositoryListener,
        HeadlampProxy? headlamp = null, VictoriaLogsProxy? logs = null)
    {
        _next = next;
        _workspace = workspace;
        _assets = assets;
        _gitRepositoryListener = gitRepositoryListener;
        _headlamp = headlamp;
        _logs = logs;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        HttpResponse response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.XFrameOptions = "DENY";
        response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self' https://ipwho.is/; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        try
        {
            HttpRequest request = context.Request;
            string path = request.Path.Value ?? "/";
            if (path.StartsWith("/internal/repository/", StringComparison.Ordinal))
            {
                if (!_gitRepositoryListener || context.Connection.LocalPort != 8788 || request.Headers.ContainsKey("Origin"))
                    throw new PublicError("not_found", "This endpoint does not exist.", 404);
                string[] segments = path.Split('/');
                if (segments.Length < 5 || !long.TryParse(segments[3], out long attemptId)) throw new PublicError("not_found", "This endpoint does not exist.", 404);
                await context.RequestServices.GetRequiredService<GitRepositoryBroker>().AuthorizeAsync(attemptId, request.Headers["X-Goblin-Repository"].ToString(), !path.EndsWith("/current", StringComparison.Ordinal), request.HttpContext.RequestAborted);
                await _next(context); return;
            }
            if (_gitRepositoryListener && context.Connection.LocalPort == 8788) throw new PublicError("not_found", "This endpoint does not exist.", 404);
            // Endpoint routing treats /work and /work/ as the same route.
            if (path == "/work/") path = "/work";
            bool get = HttpMethods.IsGet(request.Method);
            bool post = HttpMethods.IsPost(request.Method);
            if (get && path is "/healthz" or "/readyz") { await _next(context); return; }
            _workspace.ValidateRequest(request);
            if (request.Path.StartsWithSegments("/headlamp", StringComparison.Ordinal))
            {
                if (_headlamp is null) throw new PublicError("cluster_unavailable", "The cluster view requires Goblin's Kubernetes installation.", 404);
                if (_workspace.SessionId(request) is null)
                {
                    if (get && request.Headers.Accept.ToString().Contains("text/html", StringComparison.Ordinal))
                    { response.Redirect("/?returnTo=headlamp"); return; }
                    throw new PublicError("workspace_locked", "Unlock the workspace to continue.", 401);
                }
                await _headlamp.SendAsync(context, _workspace); return;
            }
            if (request.Path.StartsWithSegments("/logs", StringComparison.Ordinal))
            {
                if (_logs is null) throw new PublicError("logs_unavailable", "The log view requires Goblin's Kubernetes installation.", 404);
                if (_workspace.SessionId(request) is null)
                {
                    if (get && request.Headers.Accept.ToString().Contains("text/html", StringComparison.Ordinal))
                    { response.Redirect("/?returnTo=logs"); return; }
                    throw new PublicError("workspace_locked", "Unlock the workspace to continue.", 401);
                }
                await _logs.SendAsync(context, _workspace); return;
            }
            bool publicRequest = (get && _assets.Contains(path)) || (path == "/api/session" && (get || post));
            if (!publicRequest)
                context.Items[SessionKey] = _workspace.SessionId(request) ?? throw new PublicError("workspace_locked", "Unlock the workspace to continue.", 401);
            if (post) context.Items[ApiRequest.BodyKey] = await ApiRequest.ReadBodyAsync(request);
            await _next(context);
        }
        catch (Exception error)
        {
            if (response.HasStarted || context.RequestAborted.IsCancellationRequested) return;
            PublicError safe = PublicError.Translate(error);
            response.StatusCode = safe.Status;
            await response.WriteAsJsonAsync(new ApiFailure(new(safe.Code, safe.Message)));
        }
    }
}
