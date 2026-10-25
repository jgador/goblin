using System;
using System.Linq;
using System.Threading.Tasks;
using Goblin.Integrations.ChatGpt;
using Goblin.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Goblin.Web;

internal static class ChatGptOAuth
{
    public const string UnlockPath = "/oauth/unlock";

    internal static void Configure(IServiceCollection services, ApplicationOptions options, ChatGptConfiguration configuration)
    {
        var keys = new ChatGptOAuthKeys(configuration, options.DataDirectory);
        services.AddSingleton<ChatGptOAuthKeys>(_ => keys);
        services.AddSingleton<IJwksDocumentSource>(keys);
        services.AddOpenIddict()
            .AddCore(core => { core.DisableEntityCaching(); core.UseEntityFrameworkCore().UseDbContext<ChatGptOAuthDbContext>().ReplaceDefaultEntities<long>(); })
            .AddServer(server =>
            {
                server.SetIssuer(new Uri(configuration.Issuer))
                    .SetConfigurationEndpointUris("/.well-known/openid-configuration", "/.well-known/oauth-authorization-server")
                    .SetAuthorizationEndpointUris(ChatGptConfiguration.AuthorizationPath)
                    .SetTokenEndpointUris(ChatGptConfiguration.TokenPath)
                    .SetJsonWebKeySetEndpointUris(ChatGptConfiguration.KeysPath)
                    .SetUserInfoEndpointUris(ChatGptConfiguration.UserInfoPath)
                    .AllowAuthorizationCodeFlow().AllowRefreshTokenFlow()
                    .RequireProofKeyForCodeExchange()
                    .RegisterScopes(ChatGptConfiguration.ReadScope, Scopes.OpenId, Scopes.OfflineAccess)
                    .RegisterResources(configuration.Resource)
                    .SetAccessTokenLifetime(TimeSpan.FromMinutes(5))
                    .SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(5))
                    .SetRefreshTokenLifetime(TimeSpan.FromDays(14))
                    .SetRefreshTokenReuseLeeway(TimeSpan.Zero)
                    .AddSigningCertificate(keys.Signing).AddEncryptionCertificate(keys.Encryption)
                    .DisableAccessTokenEncryption();
                server.Configure(o =>
                {
                    o.CodeChallengeMethods.Clear();
                    o.CodeChallengeMethods.Add(CodeChallengeMethods.Sha256);
                    o.ClientAuthenticationMethods.Clear();
                    o.ClientAuthenticationMethods.Add(ClientAuthenticationMethods.ClientSecretBasic);
                });
                server.AddEventHandler<OpenIddictServerEvents.ValidateAuthorizationRequestContext>(handler => handler
                    .UseInlineHandler(context =>
                    {
                        if (!context.Request.GetResources().SequenceEqual(new[] { configuration.Resource }))
                            context.Reject(Errors.InvalidTarget, "Request the exact Goblin MCP resource.");
                        else if (!context.Request.HasScope(ChatGptConfiguration.ReadScope))
                            context.Reject(Errors.InvalidScope, "Request the Goblin Work read scope.");
                        return default;
                    }));
                server.AddEventHandler<OpenIddictServerEvents.ValidateTokenRequestContext>(handler => handler
                    .UseInlineHandler(context =>
                    {
                        if (!context.Request.GetResources().SequenceEqual(new[] { configuration.Resource }))
                            context.Reject(Errors.InvalidTarget, "Request the exact Goblin MCP resource.");
                        return default;
                    }));
                server.UseAspNetCore().EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough().EnableUserInfoEndpointPassthrough();
            })
            .AddValidation(validation =>
            {
                validation.UseLocalServer();
                validation.AddAudiences(configuration.Resource);
                validation.EnableTokenEntryValidation();
                validation.EnableAuthorizationEntryValidation();
                validation.UseAspNetCore();
            });
        services.AddScoped<ChatGptOAuthClient>();
    }

    internal static bool IsPath(PathString path) => path.StartsWithSegments("/oauth", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/.well-known/openid-configuration", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/.well-known/oauth-authorization-server", StringComparison.OrdinalIgnoreCase);

    internal static async Task InvokeAsync(HttpContext context, ChatGptConfiguration configuration, RequestDelegate next)
    {
        HttpRequest request = context.Request;
        if (!string.Equals(request.Host.Value, new Uri(configuration.Origin).Authority, StringComparison.OrdinalIgnoreCase) ||
            request.Headers.TryGetValue("Origin", out StringValues origin) && (origin.Count != 1 || origin[0] != configuration.Origin))
            throw new PublicError("invalid_origin", "Use the configured Goblin connection address.", 403);
        if (!request.IsHttps) throw new PublicError("https_required", "OAuth requires the HTTPS Goblin address.", 403);
        bool ownerForm = request.Path.StartsWithSegments(ChatGptConfiguration.AuthorizationPath, StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments(UnlockPath, StringComparison.OrdinalIgnoreCase);
        // Browsers otherwise send Origin: null for forms under no-referrer.
        // Same-origin preserves form CSRF checks without leaking OAuth URLs to ChatGPT.
        if (ownerForm)
            context.Response.Headers["Referrer-Policy"] = "same-origin";
        if (request.QueryString.Value?.Length > 8192) throw new PublicError("invalid_request", "The authorization request is too large.");
        if (HttpMethods.IsPost(request.Method))
        {
            if (ownerForm)
                context.RequestServices.GetRequiredService<Workspace>().ValidateRequest(request);
            byte[] body = await ApiRequest.ReadOAuthFormBodyAsync(request);
            using var stream = new System.IO.MemoryStream(body, writable: false);
            request.Body = stream;
            request.ContentLength = body.Length;
            await next(context);
        }
        else await next(context);
    }

    internal static void Map(WebApplication app)
    {
        app.MapMethods(ChatGptConfiguration.AuthorizationPath, ["GET", "POST"], ChatGptOAuthEndpoints.AuthorizeAsync);
        app.MapPost(UnlockPath, ChatGptOAuthEndpoints.UnlockAsync);
        app.MapPost(ChatGptConfiguration.TokenPath, ChatGptOAuthEndpoints.TokenAsync);
        app.MapGet(ChatGptConfiguration.UserInfoPath, (Delegate)ChatGptOAuthEndpoints.UserInfoAsync);
    }
}
