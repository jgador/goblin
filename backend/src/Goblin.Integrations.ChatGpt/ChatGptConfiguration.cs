using System;
using System.Linq;
using System.Text;

namespace Goblin.Integrations.ChatGpt;

public sealed class ChatGptConfiguration
{
    public const string ReadScope = "goblin.work.read";
    public const string MetadataPath = "/.well-known/oauth-protected-resource/mcp";
    public const string Description = "Read Work shared from your Goblin instance: status, attention, and explicitly shared approved results.";

    public ChatGptConfiguration(string issuer, string publicOrigin)
    {
        Uri origin = OAuthDiscovery.Address(publicOrigin);
        Uri authority = OAuthDiscovery.Address(issuer);
        if (origin.AbsolutePath != "/" || authority.AbsolutePath.Split('/').Length != 3 ||
            !authority.AbsolutePath.StartsWith("/realms/", StringComparison.Ordinal) ||
            authority.AbsolutePath.EndsWith('/') || issuer != authority.AbsoluteUri ||
            authority.AbsolutePath.Any(c => c is '%' or '.' || char.IsWhiteSpace(c)))
            throw new ArgumentException("Configure an exact HTTPS Keycloak realm issuer and Goblin public origin.");
        Origin = origin.GetLeftPart(UriPartial.Authority);
        Issuer = issuer;
        Resource = Origin + "/mcp";
        // Keep the compound PostgreSQL grant identity comfortably below its
        // btree entry limit, including a 256-character Unicode subject.
        if (Encoding.UTF8.GetByteCount(Issuer) > 512 || Encoding.UTF8.GetByteCount(Resource) > 512)
            throw new ArgumentException("The OAuth issuer and MCP resource must each fit within 512 UTF-8 bytes.");
        Discovery = new Uri(issuer + "/.well-known/openid-configuration");
        Provider = new OAuthDiscoverySummary(issuer, new Uri(issuer + "/protocol/openid-connect/auth"),
            new Uri(issuer + "/protocol/openid-connect/token"), new Uri(issuer + "/protocol/openid-connect/certs"), false);
    }

    public string Issuer { get; }
    public string Origin { get; }
    public string Resource { get; }
    public Uri Discovery { get; }
    internal OAuthDiscoverySummary Provider { get; }
    public string Challenge => $"Bearer resource_metadata=\"{Origin + MetadataPath}\", scope=\"{ReadScope}\"";
}
