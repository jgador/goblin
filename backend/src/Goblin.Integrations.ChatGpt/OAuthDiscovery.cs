using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Goblin.Integrations.ChatGpt;

public sealed class OAuthDiscoverySummary
{
    internal OAuthDiscoverySummary(string issuer, Uri authorization, Uri token, Uri keys,
        bool advertisesAuthorizationResponseIssuerIdentification)
    {
        Issuer = issuer;
        AuthorizationEndpoint = authorization;
        TokenEndpoint = token;
        JwksEndpoint = keys;
        AdvertisesAuthorizationResponseIssuerIdentification = advertisesAuthorizationResponseIssuerIdentification;
    }

    public string Issuer { get; }
    public Uri AuthorizationEndpoint { get; }
    public Uri TokenEndpoint { get; }
    public Uri JwksEndpoint { get; }

    // Caller-supplied advertisement only, not verified callback behavior or
    // permission to choose a production redirect URI or authorize Work.
    public bool AdvertisesAuthorizationResponseIssuerIdentification { get; }
}

// Validate discovery for the pre-registered confidential client. The metadata
// describes capabilities; a real code exchange verifies client enforcement.
public sealed class OAuthDiscovery
{
    internal const int MaximumMetadataBytes = 16384;
    private readonly string _issuer;
    private readonly Uri _origin;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowDuplicateProperties = false,
        MaxDepth = 8
    };

    public OAuthDiscovery(string issuer)
    {
        _origin = Address(issuer);
        _issuer = issuer;
    }

    public OAuthDiscoverySummary Review(string metadataJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(metadataJson) || StrictUtf8.GetByteCount(metadataJson) > MaximumMetadataBytes)
                throw Denied();
            Metadata metadata = JsonSerializer.Deserialize<Metadata>(metadataJson, JsonOptions) ?? throw Denied();
            if (metadata.Issuer != _issuer || !Includes(metadata.CodeChallengeMethods, "S256") ||
                !Includes(metadata.GrantTypes, "authorization_code") || !Includes(metadata.ResponseTypes, "code") ||
                !Includes(metadata.TokenAuthMethods, "client_secret_basic") || !Includes(metadata.Scopes, ChatGptConfiguration.ReadScope))
                throw Denied();
            return new(_issuer, Endpoint(metadata.AuthorizationEndpoint), Endpoint(metadata.TokenEndpoint), Endpoint(metadata.JwksUri),
                metadata.AuthorizationResponseIssParameterSupported);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            // Never retain raw metadata, URLs or parser exception details.
            throw Denied();
        }
    }

    private Uri Endpoint(string? value)
    {
        Uri address = Address(value);
        if (address.GetLeftPart(UriPartial.Authority) != _origin.GetLeftPart(UriPartial.Authority))
            throw Denied();
        return address;
    }

    internal static Uri Address(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Contains('\\') ||
            StrictUtf8.GetByteCount(value) > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out Uri? address) ||
            address.Scheme != "https" || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw Denied();
        return address;
    }

    private static bool Includes(string[]? values, string required) => values?.Contains(required, StringComparer.Ordinal) == true;

    private static InvalidOperationException Denied() => new("Customer OAuth discovery does not satisfy the configured preflight policy.");

    private sealed class Metadata
    {
        [JsonPropertyName("issuer")]
        public string? Issuer { get; init; }

        // Non-nullable Boolean: absent defaults false; explicit null or other
        // JSON types are rejected by the ordinary typed deserializer.
        [JsonPropertyName("authorization_response_iss_parameter_supported")]
        public bool AuthorizationResponseIssParameterSupported { get; init; }

        [JsonPropertyName("authorization_endpoint")]
        public string? AuthorizationEndpoint { get; init; }

        [JsonPropertyName("token_endpoint")]
        public string? TokenEndpoint { get; init; }

        [JsonPropertyName("jwks_uri")]
        public string? JwksUri { get; init; }

        [JsonPropertyName("code_challenge_methods_supported")]
        public string[]? CodeChallengeMethods { get; init; }

        [JsonPropertyName("grant_types_supported")]
        public string[]? GrantTypes { get; init; }

        [JsonPropertyName("response_types_supported")]
        public string[]? ResponseTypes { get; init; }

        [JsonPropertyName("token_endpoint_auth_methods_supported")]
        public string[]? TokenAuthMethods { get; init; }

        [JsonPropertyName("scopes_supported")]
        public string[]? Scopes { get; init; }
    }
}
