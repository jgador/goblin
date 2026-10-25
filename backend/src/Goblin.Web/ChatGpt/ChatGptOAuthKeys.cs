using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Integrations.ChatGpt;
using Microsoft.IdentityModel.Tokens;

namespace Goblin.Web;

internal sealed class ChatGptOAuthKeys : IJwksDocumentSource, IDisposable
{
    private readonly ChatGptConfiguration _configuration;
    public X509Certificate2 Signing { get; }
    public X509Certificate2 Encryption { get; }
    public string OwnerSubject { get; }

    public ChatGptOAuthKeys(ChatGptConfiguration configuration, string dataDirectory)
    {
        _configuration = configuration;
        string directory = Path.Combine(dataDirectory, "chatgpt-oauth");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        OwnerSubject = System.Text.Encoding.UTF8.GetString(ReadOrCreate(Path.Combine(directory, "owner-subject"),
            () => System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        if (!Guid.TryParseExact(OwnerSubject, "N", out _)) throw new InvalidOperationException("The saved OAuth owner identity is invalid.");
        Signing = Load(Path.Combine(directory, "signing.pfx"), "Goblin OAuth signing", X509KeyUsageFlags.DigitalSignature);
        try { Encryption = Load(Path.Combine(directory, "encryption.pfx"), "Goblin OAuth encryption", X509KeyUsageFlags.KeyEncipherment); }
        catch { Signing.Dispose(); throw; }
    }

    public Task<string> ReadAsync(Uri pinnedAddress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (pinnedAddress != new Uri(_configuration.Origin + ChatGptConfiguration.KeysPath)) throw new ChatGptAuthenticationFailure();
        using RSA rsa = Signing.GetRSAPublicKey()!;
        RSAParameters key = rsa.ExportParameters(false);
        return Task.FromResult(JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = new X509SecurityKey(Signing).KeyId, use = "sig", alg = "RS256", n = Base64UrlEncoder.Encode(key.Modulus!), e = Base64UrlEncoder.Encode(key.Exponent!) } } }));
    }

    private static X509Certificate2 Load(string path, string name, X509KeyUsageFlags usage)
    {
        byte[] bytes = ReadOrCreate(path, () =>
        {
            using RSA rsa = RSA.Create(3072);
            var request = new CertificateRequest("CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, true));
            using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
            return certificate.Export(X509ContentType.Pfx);
        });
        X509Certificate2 result = X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.EphemeralKeySet);
        using RSA? publicKey = result.GetRSAPublicKey();
        if (!result.HasPrivateKey || result.NotBefore.ToUniversalTime() > DateTime.UtcNow || result.NotAfter.ToUniversalTime() <= DateTime.UtcNow || publicKey is null)
        {
            result.Dispose();
            throw new InvalidOperationException("Restore valid persistent Goblin OAuth keys before enabling ChatGPT.");
        }
        return result;
    }

    private static byte[] ReadOrCreate(string path, Func<byte[]> create)
    {
        if (!File.Exists(path))
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N");
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temporary, options)) { stream.Write(create()); stream.Flush(true); }
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { File.Delete(temporary); }
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return File.ReadAllBytes(path);
    }

    public void Dispose() { Signing.Dispose(); Encryption.Dispose(); }
}
