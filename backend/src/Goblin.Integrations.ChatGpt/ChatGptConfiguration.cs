using System;
using System.Linq;
using System.Text;

namespace Goblin.Integrations.ChatGpt;

public sealed class ChatGptConfiguration
{
    public const string ReadScope = "goblin.work.read";
    public const string AuthorizationScopes = "openid offline_access " + ReadScope;
    public const string MetadataPath = "/.well-known/oauth-protected-resource/mcp";
    public const string Description = "Read Work shared from your Goblin instance: status, attention, and explicitly shared approved results.";

    public const string ClientId = "goblin-chatgpt";
    public const string AuthorizationPath = "/oauth/authorize";
    public const string TokenPath = "/oauth/token";
    public const string KeysPath = "/oauth/keys";
    public const string UserInfoPath = "/oauth/userinfo";

    public ChatGptConfiguration(string publicOrigin)
    {
        if (string.IsNullOrWhiteSpace(publicOrigin) || publicOrigin.Any(char.IsControl) || publicOrigin.Contains('\\') ||
            !Uri.TryCreate(publicOrigin, UriKind.Absolute, out Uri? origin) || origin.Scheme != "https" ||
            origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.AbsolutePath != "/")
            throw new ArgumentException("Configure an HTTPS Goblin public origin without a path.");
        Origin = origin.GetLeftPart(UriPartial.Authority);
        Issuer = Origin + "/";
        Resource = Origin + "/mcp";
        // Keep the compound PostgreSQL grant identity comfortably below its
        // btree entry limit, including a 256-character Unicode subject.
        if (Encoding.UTF8.GetByteCount(Issuer) > 512 || Encoding.UTF8.GetByteCount(Resource) > 512)
            throw new ArgumentException("The OAuth issuer and MCP resource must each fit within 512 UTF-8 bytes.");
        Discovery = new Uri(Origin + "/.well-known/openid-configuration");
    }

    public string Issuer { get; }
    public string Origin { get; }
    public string Resource { get; }
    public Uri Discovery { get; }
    public string Challenge => $"Bearer resource_metadata=\"{Origin + MetadataPath}\", scope=\"{ReadScope}\"";
}
