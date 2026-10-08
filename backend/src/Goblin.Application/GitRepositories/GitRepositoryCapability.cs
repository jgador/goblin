using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Goblin.Application.GitRepositories;

// Owns the local secret and the scope of repository-operation capabilities.
// Capabilities authorize only one Work attempt and GitHub account generation.
internal sealed class GitRepositoryCapability
{
    private readonly byte[] _key;

    internal GitRepositoryCapability(string keyPath)
    {
        if (!File.Exists(keyPath))
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using FileStream file = new(keyPath, options);
            file.Write(RandomNumberGenerator.GetBytes(32));
        }
        _key = File.ReadAllBytes(keyPath);
    }

    internal string Issue(long workId, long attemptId, string generation) =>
        Convert.ToHexString(Sign(workId, attemptId, generation));

    internal bool IsValid(string capability, long workId, long attemptId, string generation)
    {
        byte[] supplied;
        try { supplied = Convert.FromHexString(capability); }
        catch { return false; }
        return CryptographicOperations.FixedTimeEquals(supplied, Sign(workId, attemptId, generation));
    }

    private byte[] Sign(long workId, long attemptId, string generation) =>
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{workId}/{attemptId}/{generation}"));
}
