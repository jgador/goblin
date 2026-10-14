using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;

namespace Goblin.Mcp.Prototype;

// The future customer-owned adapter must honor cancellation, cap bytes while
// reading, enforce TLS and the pinned address, and never follow redirects.
// No concrete network adapter is registered or implemented in this increment.
public interface ICustomerJwksDocumentSource
{
    Task<string> ReadAsync(Uri pinnedAddress, CancellationToken token);
}

public sealed class CustomerSigningKeyCache
{
    private readonly OAuthDiscoverySummary _configuration;
    private readonly ICustomerJwksDocumentSource _source;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, RSAParameters> _keys = new(StringComparer.Ordinal);
    private long? _loadedAt;
    private long? _attemptAt;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { AllowDuplicateProperties = false, MaxDepth = 8 };

    public CustomerSigningKeyCache(OAuthDiscoverySummary configuration, ICustomerJwksDocumentSource source,
        TimeProvider? clock = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _clock = clock ?? TimeProvider.System;
    }

    public string Issuer => _configuration.Issuer;

    public async Task<RsaSecurityKey> GetAsync(string kid, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ValidKid(kid)) throw new ConnectorAuthenticationFailure();
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(5), token)) throw new ConnectorAuthenticationFailure();
        try
        {
            bool fresh = _loadedAt is long loaded && _clock.GetElapsedTime(loaded) < TimeSpan.FromMinutes(10);
            if (fresh && _keys.TryGetValue(kid, out RSAParameters current)) return Copy(kid, current);
            if (_attemptAt is long attempted && _clock.GetElapsedTime(attempted) < TimeSpan.FromMinutes(1))
                throw new ConnectorAuthenticationFailure();
            _attemptAt = _clock.GetTimestamp();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            string document = await _source.ReadAsync(_configuration.JwksEndpoint, deadline.Token).WaitAsync(deadline.Token);
            Dictionary<string, RSAParameters> replacement = Parse(document);
            _keys = replacement;
            _loadedAt = _clock.GetTimestamp();
            if (!_keys.TryGetValue(kid, out RSAParameters selected)) throw new ConnectorAuthenticationFailure();
            return Copy(kid, selected);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new ConnectorAuthenticationFailure(); }
        finally { _gate.Release(); }
    }

    private static Dictionary<string, RSAParameters> Parse(string document)
    {
        if (string.IsNullOrWhiteSpace(document) || StrictUtf8.GetByteCount(document) > 32768)
            throw new ConnectorAuthenticationFailure();
        // Attribute-based shape validates duplicates/depth before JOSE parsing.
        JwksDocument shape = JsonSerializer.Deserialize<JwksDocument>(document, JsonOptions) ?? throw new ConnectorAuthenticationFailure();
        if (shape.Keys is null || shape.Keys.Length is < 1 or > 16) throw new ConnectorAuthenticationFailure();
        var keys = new JsonWebKeySet(document).Keys;
        var result = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonWebKey key in keys)
        {
            if (!ValidKid(key.Kid) || !ids.Add(key.Kid) || key.HasPrivateKey ||
                key.D is not null || key.P is not null || key.Q is not null || key.DP is not null ||
                key.DQ is not null || key.QI is not null || key.K is not null)
                throw new ConnectorAuthenticationFailure();
            if (key.Kty != "RSA" || (!string.IsNullOrEmpty(key.Use) && key.Use != "sig") ||
                (!string.IsNullOrEmpty(key.Alg) && key.Alg != SecurityAlgorithms.RsaSha256) ||
                (key.KeyOps.Count != 0 && (key.KeyOps.Count != 1 || key.KeyOps[0] != "verify"))) continue;
            byte[] modulus = Base64UrlEncoder.DecodeBytes(key.N);
            byte[] exponent = Base64UrlEncoder.DecodeBytes(key.E);
            if (modulus.Length is < 256 or > 512 || modulus[0] < 128 || exponent.Length is < 1 or > 4)
                throw new ConnectorAuthenticationFailure();
            var parameters = new RSAParameters { Modulus = modulus, Exponent = exponent };
            using RSA probe = RSA.Create();
            probe.ImportParameters(parameters);
            result.Add(key.Kid, parameters);
        }
        if (result.Count == 0) throw new ConnectorAuthenticationFailure();
        return result;
    }

    private static RsaSecurityKey Copy(string kid, RSAParameters key) => new(new RSAParameters
    {
        Modulus = key.Modulus!.ToArray(),
        Exponent = key.Exponent!.ToArray()
    }) { KeyId = kid };

    private static bool ValidKid(string? kid) => !string.IsNullOrWhiteSpace(kid) && kid.Length <= 128 && !kid.Any(char.IsControl);

    private sealed class JwksDocument
    {
        [JsonPropertyName("keys")]
        public JsonWebKey[]? Keys { get; init; }
    }
}
