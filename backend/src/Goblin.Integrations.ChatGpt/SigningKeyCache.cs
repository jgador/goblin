using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;

namespace Goblin.Integrations.ChatGpt;

// Production reads public keys from the embedded server's persistent keys.
// Sources must honor cancellation and the pinned local-server address.
public interface IJwksDocumentSource
{
    Task<string> ReadAsync(Uri pinnedAddress, CancellationToken token);
}

public sealed class SigningKeyCache
{
    private readonly ChatGptConfiguration _configuration;
    private readonly IJwksDocumentSource _source;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateGate = new();
    private object _generation = new();
    private Dictionary<string, RSAParameters> _keys = new(StringComparer.Ordinal);
    private long? _loadedAt;
    private long? _attemptAt;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { AllowDuplicateProperties = false, MaxDepth = 8 };

    public SigningKeyCache(ChatGptConfiguration configuration, IJwksDocumentSource source,
        TimeProvider? clock = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _clock = clock ?? TimeProvider.System;
    }

    public string Issuer => _configuration.Issuer;

    public async Task<RsaSecurityKey> GetAsync(string kid, CancellationToken token) =>
        (await GetLeaseAsync(kid, token)).PublicKey;

    internal async Task<SigningKeyLease> GetLeaseAsync(string kid, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ValidKid(kid)) throw new ChatGptAuthenticationFailure();
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(5), token)) throw new ChatGptAuthenticationFailure();
        try
        {
            object generation;
            lock (_stateGate)
            {
                token.ThrowIfCancellationRequested();
                bool fresh = _loadedAt is long loaded && _clock.GetElapsedTime(loaded) < TimeSpan.FromMinutes(10);
                if (fresh && _keys.TryGetValue(kid, out RSAParameters current))
                    return new(this, _generation, Copy(kid, current));
                if (_attemptAt is long attempted && _clock.GetElapsedTime(attempted) < TimeSpan.FromMinutes(1))
                    throw new ChatGptAuthenticationFailure();
                _attemptAt = _clock.GetTimestamp();
                generation = _generation;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            string document = await _source.ReadAsync(new Uri(_configuration.Origin + ChatGptConfiguration.KeysPath), deadline.Token).WaitAsync(deadline.Token);
            Dictionary<string, RSAParameters> replacement = Parse(document);
            lock (_stateGate)
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(generation, _generation)) throw new ChatGptAuthenticationFailure();
                _keys = replacement;
                _loadedAt = _clock.GetTimestamp();
                // A replacement retires all prior leases, even when keys overlap.
                _generation = new object();
                if (!_keys.TryGetValue(kid, out RSAParameters selected)) throw new ChatGptAuthenticationFailure();
                return new(this, _generation, Copy(kid, selected));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new ChatGptAuthenticationFailure(); }
        finally { _gate.Release(); }
    }

    internal void RequireCurrent(object generation, CancellationToken token)
    {
        lock (_stateGate)
        {
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(generation, _generation) || _loadedAt is not long loaded ||
                _clock.GetElapsedTime(loaded) >= TimeSpan.FromMinutes(10))
                throw new ChatGptAuthenticationFailure();
        }
    }

    // Reachable only through trusted owner administration (and friend tests).
    // Does not wait for the refresh gate, run cancellation callbacks or fetch.
    internal void Invalidate(CancellationToken token)
    {
        lock (_stateGate)
        {
            token.ThrowIfCancellationRequested();
            _generation = new object();
            _keys = new(StringComparer.Ordinal);
            _loadedAt = null;
            _attemptAt = null;
        }
    }

    private static Dictionary<string, RSAParameters> Parse(string document)
    {
        if (string.IsNullOrWhiteSpace(document) || StrictUtf8.GetByteCount(document) > 32768)
            throw new ChatGptAuthenticationFailure();
        // Attribute-based shape validates duplicates/depth before JOSE parsing.
        JwksDocument shape = JsonSerializer.Deserialize<JwksDocument>(document, JsonOptions) ?? throw new ChatGptAuthenticationFailure();
        if (shape.Keys is null || shape.Keys.Length is < 1 or > 16) throw new ChatGptAuthenticationFailure();
        IList<JsonWebKey> keys = new JsonWebKeySet(document).Keys;
        var result = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonWebKey key in keys)
        {
            if (!ValidKid(key.Kid) || !ids.Add(key.Kid) || key.HasPrivateKey ||
                key.D is not null || key.P is not null || key.Q is not null || key.DP is not null ||
                key.DQ is not null || key.QI is not null || key.K is not null)
                throw new ChatGptAuthenticationFailure();
            if (key.Kty != "RSA" || (!string.IsNullOrEmpty(key.Use) && key.Use != "sig") ||
                (!string.IsNullOrEmpty(key.Alg) && key.Alg != SecurityAlgorithms.RsaSha256) ||
                (key.KeyOps.Count != 0 && (key.KeyOps.Count != 1 || key.KeyOps[0] != "verify"))) continue;
            byte[] modulus = Base64UrlEncoder.DecodeBytes(key.N);
            byte[] exponent = Base64UrlEncoder.DecodeBytes(key.E);
            if (modulus.Length is < 256 or > 512 || modulus[0] < 128 || exponent.Length is < 1 or > 4)
                throw new ChatGptAuthenticationFailure();
            var parameters = new RSAParameters { Modulus = modulus, Exponent = exponent };
            using RSA probe = RSA.Create();
            probe.ImportParameters(parameters);
            result.Add(key.Kid, parameters);
        }
        if (result.Count == 0) throw new ChatGptAuthenticationFailure();
        return result;
    }

    private static RsaSecurityKey Copy(string kid, RSAParameters key) => new(new RSAParameters
    {
        Modulus = [.. key.Modulus!],
        Exponent = [.. key.Exponent!]
    })
    { KeyId = kid };

    private static bool ValidKid(string? kid) => !string.IsNullOrWhiteSpace(kid) && kid.Length <= 128 && !kid.Any(char.IsControl);

    private sealed class JwksDocument
    {
        [JsonPropertyName("keys")]
        public JsonWebKey[]? Keys { get; init; }
    }
}

internal sealed class SigningKeyLease
{
    private readonly SigningKeyCache _owner;
    private readonly object _generation;

    internal SigningKeyLease(SigningKeyCache owner, object generation, RsaSecurityKey publicKey)
    {
        _owner = owner;
        _generation = generation;
        PublicKey = publicKey;
    }

    internal RsaSecurityKey PublicKey { get; }

    internal void RequireCurrent(SigningKeyCache expectedOwner, CancellationToken token)
    {
        if (!ReferenceEquals(_owner, expectedOwner)) throw new ChatGptAuthenticationFailure();
        _owner.RequireCurrent(_generation, token);
    }
}
