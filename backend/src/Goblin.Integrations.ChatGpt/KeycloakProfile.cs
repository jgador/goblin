using System;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Goblin.Integrations.ChatGpt;

// Import into a new, customer-owned realm. Never contains passwords or secrets.
public static partial class KeycloakProfile
{
    public const string Version = "26.8.1";
    public const string ClientId = "goblin-chatgpt";

    public static string Create(ChatGptConfiguration configuration, string? redirectUri)
    {
        if (!string.IsNullOrEmpty(redirectUri) && !Callback().IsMatch(redirectUri))
            throw new ArgumentException("Copy the exact ChatGPT OAuth redirect URI from the connection management page.");
        string realm = new Uri(configuration.Issuer).AbsolutePath["/realms/".Length..];
        var attributes = new System.Collections.Generic.Dictionary<string, string>
        {
            ["pkce.code.challenge.method"] = "S256",
            ["access.token.header.type.rfc9068"] = "true",
            ["access.token.signed.response.alg"] = "RS256",
            ["exclude.issuer.from.auth.response"] = "false"
        };
        return JsonSerializer.Serialize(new
        {
            realm,
            enabled = true,
            sslRequired = "all",
            registrationAllowed = false,
            resetPasswordAllowed = false,
            accessTokenLifespan = 300,
            revokeRefreshToken = true,
            refreshTokenMaxReuse = 0,
            clientScopes = new object[]
            {
                new
                {
                    name = ChatGptConfiguration.ReadScope,
                    description = "Read Work explicitly shared by the Goblin owner",
                    protocol = "openid-connect",
                    attributes = new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["include.in.token.scope"] = "true", ["display.on.consent.screen"] = "true",
                        ["consent.screen.text"] = "Read Work shared with you in Goblin"
                    }
                },
                new
                {
                    name = "email", protocol = "openid-connect",
                    attributes = new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["include.in.token.scope"] = "true", ["display.on.consent.screen"] = "true",
                        ["consent.screen.text"] = "Share your verified email with ChatGPT"
                    },
                    protocolMappers = new[] { EmailMapper("email", "email", "String"), EmailMapper("email verified", "emailVerified", "boolean") }
                }
            },
            clients = new object[]
            {
                new
                {
                    clientId = "goblin-mcp", name = "Goblin MCP resource", protocol = "openid-connect",
                    enabled = true, bearerOnly = true, standardFlowEnabled = false,
                    directAccessGrantsEnabled = false, implicitFlowEnabled = false, serviceAccountsEnabled = false,
                    attributes = new System.Collections.Generic.Dictionary<string, string> { ["resource_url"] = configuration.Resource }
                },
                new
                {
                    clientId = ClientId, name = "ChatGPT for Goblin", protocol = "openid-connect",
                    enabled = true, publicClient = false, clientAuthenticatorType = "client-secret", standardFlowEnabled = true,
                    directAccessGrantsEnabled = false, implicitFlowEnabled = false, serviceAccountsEnabled = false,
                    consentRequired = true, fullScopeAllowed = false,
                    redirectUris = string.IsNullOrEmpty(redirectUri) ? [] : new[] { redirectUri },
                    webOrigins = Array.Empty<string>(), attributes,
                    defaultClientScopes = new[] { ChatGptConfiguration.ReadScope },
                    optionalClientScopes = new[] { "email" },
                    protocolMappers = new[]
                    {
                        new
                        {
                            name = "Stable subject", protocol = "openid-connect", protocolMapper = "oidc-sub-mapper",
                            config = new System.Collections.Generic.Dictionary<string, string>
                            {
                                ["access.token.claim"] = "true", ["introspection.token.claim"] = "true"
                            }
                        },
                        new
                        {
                            name = "Goblin resource audience", protocol = "openid-connect", protocolMapper = "oidc-audience-mapper",
                            config = new System.Collections.Generic.Dictionary<string, string>
                            {
                                ["included.client.audience"] = "goblin-mcp",
                                ["access.token.claim"] = "true", ["id.token.claim"] = "false"
                            }
                        }
                    }
                }
            }
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object EmailMapper(string name, string property, string type) => new
    {
        name,
        protocol = "openid-connect",
        protocolMapper = "oidc-usermodel-property-mapper",
        config = new System.Collections.Generic.Dictionary<string, string>
        {
            ["user.attribute"] = property,
            ["claim.name"] = property == "emailVerified" ? "email_verified" : "email",
            ["jsonType.label"] = type,
            ["id.token.claim"] = "true",
            ["access.token.claim"] = "false",
            ["userinfo.token.claim"] = "true"
        }
    };

    [GeneratedRegex("^https://chatgpt\\.com/(connector_platform_oauth_redirect|connector/oauth/[A-Za-z0-9_-]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Callback();
}
