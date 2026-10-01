using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Goblin.Integrations.Slack;

// The key and ciphertext belong to this deployment's private, persistent data
// directory. Neither is exported to agent workspaces or public configuration.
public sealed class SlackCredentialStore
{
    private readonly string _directory;

    private string KeyPath => Path.Combine(_directory, "key");

    private string CredentialPath => Path.Combine(_directory, "credentials");

    public SlackCredentialStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        PrivateDirectory(_directory);
    }

    public SlackCredentials? Read()
    {
        if (!File.Exists(CredentialPath)) return null;
        try
        {
            SafePath(CredentialPath); SafePath(KeyPath);
            byte[] bytes = File.ReadAllBytes(CredentialPath), key = File.ReadAllBytes(KeyPath);
            if (bytes.Length is < 29 or > 16384) throw new CryptographicException();
            byte[] clear = new byte[bytes.Length - 28];
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), clear, "goblin.slack.v1"u8);
                return JsonSerializer.Deserialize<SlackCredentials>(clear) ?? throw new CryptographicException();
            }
            finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(clear); }
        }
        catch (Exception error) when (error is IOException or CryptographicException or JsonException)
        { throw new SlackFailure("The saved Slack credentials could not be opened. Restore this deployment’s key or reconnect Slack."); }
    }

    public async Task SaveAsync(SlackCredentials credentials, CancellationToken token)
    {
        SafePath(KeyPath);
        if (!File.Exists(KeyPath))
        {
            if (File.Exists(CredentialPath)) throw new SlackFailure("Restore the Slack encryption key before replacing saved credentials.");
            await WriteAsync(KeyPath, RandomNumberGenerator.GetBytes(32), token);
        }
        byte[] key = await File.ReadAllBytesAsync(KeyPath, token);
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            byte[] bytes = new byte[clear.Length + 28];
            RandomNumberGenerator.Fill(bytes.AsSpan(0, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(bytes.AsSpan(0, 12), clear, bytes.AsSpan(28), bytes.AsSpan(12, 16), "goblin.slack.v1"u8);
            await WriteAsync(CredentialPath, bytes, token);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(clear); }
    }

    public void Clear() { SafePath(CredentialPath); File.Delete(CredentialPath); }

    internal static void PrivateDirectory(string path)
    {
        SafePath(path);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    internal static void SafePath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null) throw new SlackFailure("Slack storage must not use symbolic links.");
    }

    internal static async Task WriteAsync(string path, byte[] bytes, CancellationToken token)
    {
        SafePath(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options)) { await stream.WriteAsync(bytes, token); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
}
