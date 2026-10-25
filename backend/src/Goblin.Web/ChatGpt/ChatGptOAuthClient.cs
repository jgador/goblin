using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Integrations.ChatGpt;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Goblin.Web;

internal sealed class ChatGptOAuthClient
{
    private readonly IOpenIddictApplicationManager _applications;
    private readonly IOpenIddictAuthorizationManager _authorizations;
    private readonly ChatGptConfiguration _configuration;

    public ChatGptOAuthClient(IOpenIddictApplicationManager applications, IOpenIddictAuthorizationManager authorizations, ChatGptConfiguration configuration)
    {
        _applications = applications;
        _authorizations = authorizations;
        _configuration = configuration;
    }

    public async Task<string?> SaveAsync(string? callback, bool rotateSecret, CancellationToken token)
    {
        if (!string.IsNullOrEmpty(callback)) ChatGptCallback.Require(callback);
        object? application = await _applications.FindByClientIdAsync(ChatGptConfiguration.ClientId, token);
        var descriptor = new OpenIddictApplicationDescriptor();
        if (application is not null) await _applications.PopulateAsync(descriptor, application, token);
        descriptor.ClientId = ChatGptConfiguration.ClientId;
        descriptor.ClientType = ClientTypes.Confidential;
        descriptor.ConsentType = ConsentTypes.Explicit;
        descriptor.DisplayName = "ChatGPT for Goblin";
        descriptor.RedirectUris.Clear();
        if (!string.IsNullOrEmpty(callback)) descriptor.RedirectUris.Add(new Uri(callback));
        descriptor.Permissions.Clear();
        descriptor.Permissions.UnionWith([Permissions.Endpoints.Authorization, Permissions.Endpoints.Token,
            Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken,
            Permissions.ResponseTypes.Code, Permissions.Prefixes.Scope + ChatGptConfiguration.ReadScope,
            Permissions.Prefixes.Resource + _configuration.Resource]);
        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
        string? secret = application is null || rotateSecret ? Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)) : null;
        if (application is null)
        {
            descriptor.ClientSecret = secret;
            await _applications.CreateAsync(descriptor, token);
        }
        else
        {
            // Descriptor secrets are hashes when populated; UpdateAsync(descriptor)
            // preserves them. Explicit rotation uses the manager's hashing overload.
            await _applications.UpdateAsync(application, descriptor, token);
            if (secret is not null)
            {
                await _applications.UpdateAsync(application, secret, token);
                await RevokeAsync(token);
            }
        }
        return secret;
    }

    public async Task RevokeAsync(CancellationToken token)
    {
        object? application = await _applications.FindByClientIdAsync(ChatGptConfiguration.ClientId, token);
        if (application is null) return;
        string? id = await _applications.GetIdAsync(application, token);
        // Finish the PostgreSQL reader before updating through the same scoped
        // context/transaction; Npgsql does not support concurrent commands.
        var current = new List<object>();
        await foreach (object authorization in _authorizations.FindByApplicationIdAsync(id!, token)) current.Add(authorization);
        foreach (object authorization in current)
        {
            if (await _authorizations.GetStatusAsync(authorization, token) == Statuses.Revoked) continue;
            if (!await _authorizations.TryRevokeAsync(authorization, token))
                throw new PublicError("oauth_revoke_failed", "Connection revocation could not be saved. Check storage and try again.", 503);
        }
    }
}
