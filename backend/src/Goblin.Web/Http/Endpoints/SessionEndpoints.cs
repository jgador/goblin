using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Goblin.Web;

internal static class SessionEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/session", Get);
        app.MapPost("/api/session", Unlock);
        app.MapPost("/api/session/lock", Lock);
    }

    private static SessionState Get(HttpContext context, Workspace workspace) =>
        workspace.Session(workspace.SessionId(context.Request) is not null);

    private static SessionState Unlock(HttpContext context, Workspace workspace) =>
        workspace.Unlock(ApiRequest.StringField(context, "password"), context.Response);

    private static SessionState Lock(HttpContext context, Workspace workspace) =>
        workspace.Lock((string)context.Items[WorkspaceMiddleware.SessionKey]!, context.Response);
}
