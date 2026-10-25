using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Goblin.Integrations.ChatGpt;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Goblin.Web;

internal static class ChatGptOAuthEndpoints
{
    private const string Scheme = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;

    internal static async Task<IResult> AuthorizeAsync(HttpContext context, Workspace workspace, ChatGptConfiguration configuration,
        ChatGptOAuthKeys keys, IOpenIddictApplicationManager applications, IOpenIddictAuthorizationManager authorizations)
    {
        OpenIddictRequest request = context.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("Missing validated OAuth request.");
        ChatGptCallback.Require(request.RedirectUri!);
        // Browsers apply form-action to the redirect after consent as well.
        // Permit only the exact registered callback on this authorization response.
        context.Response.Headers.ContentSecurityPolicy = context.Response.Headers.ContentSecurityPolicy.ToString()
            .Replace("form-action 'self'", "form-action 'self' " + request.RedirectUri, StringComparison.Ordinal);
        if (workspace.SessionId(context.Request) is null)
        {
            if (request.HasPromptValue(PromptValues.None)) return Reject(Errors.LoginRequired, "Unlock Goblin to connect ChatGPT.");
            return Page("Unlock Goblin", "Enter your Goblin owner password to connect ChatGPT.",
                $"<form method=\"post\" action=\"{ChatGptOAuth.UnlockPath}\"><input type=\"hidden\" name=\"returnTo\" value=\"{Encode(ReturnTo(request))}\"><label>Goblin password<input name=\"password\" type=\"password\" required maxlength=\"128\" autocomplete=\"current-password\" autofocus></label><button>Unlock Goblin</button></form>");
        }
        if (request.HasPromptValue(PromptValues.None)) return Reject(Errors.ConsentRequired, "Approve the ChatGPT connection in Goblin.");
        IFormCollection? form = HttpMethods.IsPost(context.Request.Method) ? await context.Request.ReadFormAsync(context.RequestAborted) : null;
        if (form?["decision"].ToString() == "deny") return Reject(Errors.AccessDenied, "The Goblin owner declined the connection.");
        if (form?["decision"].ToString() != "allow")
        {
            string hidden = string.Concat(request.GetParameters().Select(p => $"<input type=\"hidden\" name=\"{Encode(p.Key)}\" value=\"{Encode((string?)p.Value ?? "")}\">"));
            return Page("Connect ChatGPT to Goblin", "ChatGPT may read Work that you explicitly share. Signing in does not share any Work or grant administration access.",
                $"<form method=\"post\" action=\"{ChatGptConfiguration.AuthorizationPath}\">{hidden}<button name=\"decision\" value=\"allow\">Allow connection</button><button name=\"decision\" value=\"deny\" class=\"secondary\">Cancel</button></form>");
        }
        var identity = new ClaimsIdentity(Scheme);
        identity.SetClaim(Claims.Subject, keys.OwnerSubject);
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());
        principal.SetResources(configuration.Resource);
        object application = await applications.FindByClientIdAsync(request.ClientId!, context.RequestAborted) ?? throw new InvalidOperationException("Missing validated OAuth client.");
        object authorization = await authorizations.CreateAsync(principal, keys.OwnerSubject,
            (await applications.GetIdAsync(application, context.RequestAborted))!, AuthorizationTypes.Permanent,
            principal.GetScopes(), context.RequestAborted);
        principal.SetAuthorizationId(await authorizations.GetIdAsync(authorization, context.RequestAborted));
        principal.SetDestinations(claim => claim.Type == Claims.Subject ? [Destinations.AccessToken, Destinations.IdentityToken] : []);
        return Results.SignIn(principal, authenticationScheme: Scheme);
    }

    internal static async Task<IResult> UnlockAsync(HttpContext context, Workspace workspace)
    {
        IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted);
        string returnTo = form["returnTo"].ToString();
        if (!returnTo.StartsWith(ChatGptConfiguration.AuthorizationPath + "?", StringComparison.Ordinal) || returnTo.Length > 8192 || returnTo.Contains('#') || returnTo.Contains('\r') || returnTo.Contains('\n'))
            throw new PublicError("invalid_request", "Start sign-in again from ChatGPT.");
        workspace.Unlock(form["password"].ToString(), context.Response);
        return Results.Redirect(returnTo);
    }

    internal static async Task<IResult> TokenAsync(HttpContext context, ChatGptConfiguration configuration, ChatGptOAuthKeys keys)
    {
        OpenIddictRequest request = context.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("Missing validated OAuth request.");
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType()) return Reject(Errors.UnsupportedGrantType, "Use authorization code or refresh token.");
        AuthenticateResult result = await context.AuthenticateAsync(Scheme);
        ClaimsPrincipal? principal = result.Principal;
        if (!result.Succeeded || principal?.GetClaim(Claims.Subject) != keys.OwnerSubject || !principal.HasScope(ChatGptConfiguration.ReadScope) ||
            !principal.GetResources().SequenceEqual(new[] { configuration.Resource }))
            return Reject(Errors.InvalidGrant, "Connect ChatGPT to Goblin again.");
        if (!string.IsNullOrEmpty(request.Scope))
        {
            if (!request.HasScope(ChatGptConfiguration.ReadScope) || request.GetScopes().Any(s => !principal.HasScope(s)))
                return Reject(Errors.InvalidScope, "Keep the granted Goblin Work read scope.");
            principal.SetScopes(request.GetScopes());
        }
        return Results.SignIn(principal, authenticationScheme: Scheme);
    }

    internal static async Task<IResult> UserInfoAsync(HttpContext context)
    {
        AuthenticateResult result = await context.AuthenticateAsync(Scheme);
        if (!result.Succeeded || result.Principal?.HasScope(Scopes.OpenId) != true) return Reject(Errors.InvalidToken, "An OpenID access token is required.");
        return Results.Json(new { sub = result.Principal.GetClaim(Claims.Subject) });
    }

    private static IResult Reject(string error, string description) => Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
    {
        [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
    }), [Scheme]);

    private static string ReturnTo(OpenIddictRequest request) => ChatGptConfiguration.AuthorizationPath + "?" +
        string.Join('&', request.GetParameters().Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString((string?)p.Value ?? "")));

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static IResult Page(string title, string description, string body) => Results.Content(
        $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{Encode(title)} · Goblin</title><link rel=\"stylesheet\" href=\"/assets/chatgpt/oauth.css\"></head><body><main><img src=\"/assets/branding/icon.svg\" alt=\"\" width=\"48\" height=\"48\"><h1>{Encode(title)}</h1><p>{Encode(description)}</p>{body}</main></body></html>", "text/html", Encoding.UTF8);
}
