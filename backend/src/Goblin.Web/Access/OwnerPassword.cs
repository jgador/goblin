using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Goblin.Web;

internal sealed class OwnerPassword
{
    private readonly byte[] _salt;
    private readonly byte[] _digest;

    public OwnerPassword(byte[] salt, byte[] digest)
    {
        _salt = salt;
        _digest = digest;
    }

    public static async Task<OwnerPassword> LoadAsync(string path)
    {
        // Every environment requires the same configured verifier.
        if (new FileInfo(path).Length > 256) throw InvalidHash();
        string[] fields = (await File.ReadAllTextAsync(path)).Trim().Split('$');
        if (fields.Length != 4 || fields[0] != "pbkdf2-sha256" || fields[1] != "600000") throw InvalidHash();
        try
        {
            byte[] salt = Convert.FromBase64String(fields[2]);
            byte[] digest = Convert.FromBase64String(fields[3]);
            if (salt.Length != 16 || digest.Length != 32) throw InvalidHash();
            return new(salt, digest);
        }
        catch (FormatException) { throw InvalidHash(); }
    }

    public bool Verify(string value)
    {
        if (value.Length is 0 or > 128) return false;
        byte[] candidate = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(value), _salt,
            600_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(candidate, _digest);
    }

    private static InvalidOperationException InvalidHash() => new("The configured Goblin password hash file is invalid.");
}
