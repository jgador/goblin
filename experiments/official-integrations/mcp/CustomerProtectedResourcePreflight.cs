using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Goblin.Mcp.Prototype;

// Offline check of the current single-issuer, single-scope private HTTP profile.
// Expected authority comes only from trusted operator settings, never this JSON.
internal sealed class CustomerProtectedResourcePreflight
{
    private readonly string _resource;
    private readonly string _issuer;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowDuplicateProperties = false,
        MaxDepth = 8
    };

    internal CustomerProtectedResourcePreflight(string resource, string issuer)
    {
        try
        {
            Uri address = CustomerOAuthDiscoveryPreflight.Address(resource);
            _ = CustomerOAuthDiscoveryPreflight.Address(issuer);
            if (address.AbsolutePath != "/mcp" || address.AbsoluteUri != resource) throw Denied();
            _resource = resource;
            _issuer = issuer;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { throw Denied(); }
    }

    internal void Review(string metadataJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(metadataJson) ||
                StrictUtf8.GetByteCount(metadataJson) > CustomerOAuthDiscoveryPreflight.MaximumMetadataBytes)
                throw Denied();
            Metadata metadata = JsonSerializer.Deserialize<Metadata>(metadataJson, JsonOptions) ?? throw Denied();
            if (metadata.Resource != _resource || metadata.AuthorizationServers is not { Length: 1 } ||
                metadata.AuthorizationServers[0] != _issuer || metadata.Scopes is not { Length: 1 } ||
                metadata.Scopes[0] != ConnectorReadBoundary.ReadScope)
                throw Denied();
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            throw Denied();
        }
    }

    private static InvalidOperationException Denied() => new("Protected-resource metadata does not satisfy the configured preflight policy.");

    private sealed class Metadata
    {
        [JsonPropertyName("resource")]
        public string? Resource { get; init; }

        [JsonPropertyName("authorization_servers")]
        public string[]? AuthorizationServers { get; init; }

        [JsonPropertyName("scopes_supported")]
        public string[]? Scopes { get; init; }
    }
}
