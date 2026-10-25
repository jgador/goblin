using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.ChatGpt;
using Goblin.Contracts;
using Goblin.Integrations.ChatGpt;
using Goblin.Persistence;
using Goblin.Persistence.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace Goblin.Web;

internal static class ChatGptEndpoints
{
    private const string Root = "/api/integrations/chatgpt";

    internal static void Map(WebApplication app)
    {
        ChatGptConfiguration? configuration = app.Services.GetService<ChatGptConfiguration>();
        app.MapGet(Root, async (HttpContext context) =>
        {
            object? client = configuration is null ? null : await context.RequestServices.GetRequiredService<IOpenIddictApplicationManager>()
                .FindByClientIdAsync(ChatGptConfiguration.ClientId, context.RequestAborted);
            string? callback = client is null ? null : (await context.RequestServices.GetRequiredService<IOpenIddictApplicationManager>()
                .GetRedirectUrisAsync(client, context.RequestAborted)).FirstOrDefault();
            ChatgptWorkGrant[] grants = configuration is null ? [] : await context.RequestServices.GetRequiredService<ChatGptGrantStore>()
                .ListAsync(configuration.Issuer, configuration.Resource, context.RequestAborted);
            return Results.Json(new ChatGptSetupView
            {
                Configured = configuration is not null,
                ServerUrl = configuration?.Resource,
                Issuer = configuration?.Issuer,
                OwnerSubject = configuration is null ? null : context.RequestServices.GetRequiredService<ChatGptOAuthKeys>().OwnerSubject,
                ClientConfigured = client is not null,
                RedirectUri = callback,
                Grants = [.. grants.Select(g => new ChatGptGrantView
                {
                    Id = g.Id.ToString(CultureInfo.InvariantCulture),
                    Subject = g.Subject,
                    WorkId = g.WorkId.ToString(CultureInfo.InvariantCulture),
                    ResultAttemptId = g.ResultAttemptId?.ToString(CultureInfo.InvariantCulture),
                    ExpiresAt = g.ExpiresAt,
                    Expired = g.ExpiresAt <= DateTime.UtcNow
                })]
            });
        });
        if (configuration is null) return;
        app.MapPost(Root + "/package", (HttpContext context) => Results.Json(ChatGptPackage.Create(configuration,
            ApiRequest.Body<ChatGptPackageRequest>(context, ContractJson.Options))));
        app.MapGet(Root + "/work", async (IDbContextFactory<GoblinDbContext> factory, CancellationToken token) =>
        {
            await using GoblinDbContext db = await factory.CreateDbContextAsync(token);
            var rows = await db.WorkItems.AsNoTracking().OrderByDescending(w => w.UpdatedAt).Take(200)
                .Select(w => new { w.Id, w.Objective }).ToArrayAsync(token);
            return Results.Json(rows.Select(w => new ChatGptWorkChoice { Id = w.Id.ToString(CultureInfo.InvariantCulture), Objective = w.Objective }));
        });
        app.MapPost(Root + "/grants", async (HttpContext context, ChatGptGrantStore store) =>
        {
            ShareRequest request = ApiRequest.Body<ShareRequest>(context, ContractJson.Options);
            string subject = context.RequestServices.GetRequiredService<ChatGptOAuthKeys>().OwnerSubject;
            await store.ApproveAsync(configuration.Issuer, configuration.Resource, subject,
                Id(request.WorkId), request.Hours, request.ShareApprovedResult, context.RequestAborted);
            return Results.Json(new { saved = true });
        });
        app.MapPost(Root + "/grants/revoke", async (HttpContext context, ChatGptGrantStore store) =>
        {
            RevokeRequest request = ApiRequest.Body<RevokeRequest>(context, ContractJson.Options);
            await store.RevokeAsync(configuration.Issuer, configuration.Resource, Id(request.Id), context.RequestAborted);
            return Results.Json(new { revoked = true });
        });
        app.MapPost(Root + "/verify", VerifyAsync);
        app.MapPost(Root + "/keys/refresh", (HttpContext context, ChatGptAuthentication authentication) =>
        {
            authentication.InvalidateKeys(context.RequestAborted);
            return Results.Json(new { refreshed = true });
        });
        app.MapPost(Root + "/client", async (HttpContext context, ChatGptOAuthClient client, ChatGptOAuthDbContext db) =>
        {
            try
            {
                ClientRequest request = ApiRequest.Body<ClientRequest>(context, ContractJson.Options);
                await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(70619284)", context.RequestAborted);
                string? secret = await client.SaveAsync(request.RedirectUri, request.RotateSecret, context.RequestAborted);
                await transaction.CommitAsync(context.RequestAborted);
                return Results.Json(new { clientSecret = secret });
            }
            catch (ArgumentException)
            {
                throw new PublicError("invalid_callback", "Copy the exact ChatGPT OAuth callback shown in connection settings. Wildcards are not accepted.");
            }
        });
        app.MapPost(Root + "/client/revoke", async (HttpContext context, ChatGptOAuthClient client) =>
        {
            await client.RevokeAsync(context.RequestAborted);
            return Results.Json(new { revoked = true });
        });
    }

    private static async Task<IResult> VerifyAsync(HttpContext context, IOptionsMonitor<OpenIddictServerOptions> options,
        ChatGptOAuthDbContext oauth, IDbContextFactory<GoblinDbContext> factory)
    {
        bool discovery = false;
        bool database = false;
        bool issuerResponses = false;
        try
        {
            OpenIddictServerOptions server = options.CurrentValue;
            discovery = server.Issuer is not null && server.CodeChallengeMethods.Contains(OpenIddictConstants.CodeChallengeMethods.Sha256) &&
                server.SigningCredentials.Count > 0 && server.ConfigurationEndpointUris.Count > 0;
            issuerResponses = true; // OpenIddict includes iss in redirect authorization responses.
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception) { }
        try
        {
            await using GoblinDbContext db = await factory.CreateDbContextAsync(context.RequestAborted);
            _ = await db.ChatgptWorkGrants.Select(g => g.Id).Take(1).ToArrayAsync(context.RequestAborted);
            _ = await oauth.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreApplication<long>>().Select(a => a.Id).Take(1).ToArrayAsync(context.RequestAborted);
            _ = await oauth.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization<long>>().Select(a => a.Id).Take(1).ToArrayAsync(context.RequestAborted);
            _ = await oauth.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken<long>>().Select(a => a.Id).Take(1).ToArrayAsync(context.RequestAborted);
            database = true;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception) { }
        return Results.Json(new ChatGptVerificationView { Discovery = discovery, Database = database, IssuerResponses = issuerResponses, CheckedAt = DateTimeOffset.UtcNow });
    }

    private static long Id(string? value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long id) &&
        id > 0 ? id : throw new PublicError("invalid_command", "Choose a saved Work item or access grant.");

    private sealed class ShareRequest
    {
        [JsonPropertyName("workId")]
        public string? WorkId { get; init; }
        [JsonPropertyName("hours")]
        public int Hours { get; init; } = 24;
        [JsonPropertyName("shareApprovedResult")]
        public bool ShareApprovedResult { get; init; }
    }

    private sealed class RevokeRequest
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }
    }

    private sealed class ClientRequest
    {
        [JsonPropertyName("redirectUri")]
        public string? RedirectUri { get; init; }

        [JsonPropertyName("rotateSecret")]
        public bool RotateSecret { get; init; }
    }
}
