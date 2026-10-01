using System;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts.Conversations;
using Goblin.Integrations.Slack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Api = Goblin.Web.Http.Contracts;

namespace Goblin.Web;

internal static class SlackEndpoints
{
    private const string Root = "/api/integrations/slack";

    public static void Map(WebApplication app, bool enabled)
    {
        if (!enabled)
        {
            app.MapGet(Root, () => WorkResponse.Json(new Api.SlackState(false, null, null, [], null)));
            return;
        }
        app.MapGet(Root, StateAsync);
        app.MapGet(Root + "/manifest", () => Results.Text(Assets.Manifest, "application/json"));
        app.MapPost(Root + "/setup", (HttpContext context, SlackSetup setup) => WorkResponse.Json(Api.SlackSetupView.From(setup.Start(Session(context)))));
        app.MapPost(Root + "/confirm", (HttpContext context, SlackSetup setup) =>
            WorkResponse.Json(Api.SlackSetupView.From(setup.Confirm(Session(context), ApiRequest.StringField(context, "code") ?? ""))));
        app.MapPost(Root + "/resume", (HttpContext context, SlackSetup setup) => WorkResponse.Json(Api.SlackSetupView.From(setup.Resume(Session(context)))));
        app.MapPost(Root + "/cancel", async (HttpContext context, SlackSetup setup) =>
        { await setup.CancelAsync(); return Results.Json(new { cancelled = true }); });
        app.MapPost(Root + "/connect", async (HttpContext context, SlackConnection connection, SlackSetup setup) =>
        {
            await setup.CancelAsync();
            await connection.ConnectAsync(ApiRequest.StringField(context, "appToken") ?? "", ApiRequest.StringField(context, "botToken") ?? "", context.RequestAborted);
            return WorkResponse.Json(Api.SlackConnectionView.From(connection.View));
        });
        app.MapPost(Root + "/disconnect", async (HttpContext context, SlackConnection connection, SlackSetup setup) =>
        {
            await setup.CancelAsync();
            await connection.DisconnectAsync(context.RequestAborted); return WorkResponse.Json(Api.SlackConnectionView.From(connection.View));
        });
        app.MapPost(Root + "/link", async (HttpContext context, SlackConnection connection, ExternalConversationStore store) =>
            WorkResponse.Json(Api.SlackLinkCode.From(await store.StartLinkAsync(Installation(connection), Session(context), context.RequestAborted))));
        app.MapPost(Root + "/link/confirm", async (HttpContext context, SlackConnection connection, ExternalConversationStore store) =>
        { await store.ConfirmLinkAsync(Installation(connection), Session(context), Id(context), context.RequestAborted); return Results.Json(new { linked = true }); });
        app.MapPost(Root + "/link/revoke", async (HttpContext context, SlackConnection connection, ExternalConversationStore store) =>
        { await store.RevokeAsync(Installation(connection), Id(context), context.RequestAborted); return Results.Json(new { revoked = true }); });
    }

    private static async Task<IResult> StateAsync(HttpContext context, SlackConnection connection, SlackSetup setup, ExternalConversationStore store, CancellationToken token)
    {
        ExternalInstallation? installation = connection.Installation;
        ExternalLinkView? link = installation is null ? null : await store.LinkAsync(installation, Session(context), token);
        return WorkResponse.Json(new Api.SlackState(true, Api.SlackConnectionView.From(connection.View), Api.SlackSetupView.From(setup.View(Session(context))),
            installation is null ? [] : Array.ConvertAll(await store.IdentitiesAsync(installation, token), Api.SlackIdentityView.From),
            link is null ? null : Api.SlackLinkView.From(link)));
    }

    private static string Session(HttpContext context) => (string)context.Items[WorkspaceMiddleware.SessionKey]!;

    private static ExternalInstallation Installation(SlackConnection connection) => connection.Installation ?? throw new SlackFailure("Connect Slack first.");

    private static long Id(HttpContext context) => long.TryParse(ApiRequest.StringField(context, "id"), out long id) && id > 0 ? id : throw new PublicError("invalid_command", "Choose a saved Slack identity.");
}
