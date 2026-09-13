using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Goblin.Web;

public sealed partial class Workspace
{
    public const string CookieName = "goblin_auth_session";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    private readonly Lock _gate = new();
    private readonly byte[]? _ownerHash;
    private readonly OwnerPassword? _ownerPassword;
    private readonly OrderedDictionary<string, DateTimeOffset> _sessions = [];
    private readonly List<DateTimeOffset> _failedUnlocks = [];
    private readonly HashSet<string> _allowedOrigins = [];
    private readonly HashSet<string> _allowedHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Uri _origin;

    private Workspace(string dataDir, string publicOrigin, string? token, OwnerPassword? password = null, bool allowInsecureHttp = false)
    {
        DataDirectory = dataDir;
        TokenFile = Path.Combine(dataDir, "owner-token");
        _ownerHash = token is null ? null : Hash(token);
        _ownerPassword = password;
        _origin = ValidateOrigin(publicOrigin, allowInsecureHttp);
        _allowedOrigins.Add(_origin.GetLeftPart(UriPartial.Authority));
        if (IsLoopback(_origin))
            foreach (var host in new[] { "localhost", "127.0.0.1", "[::1]" })
                _allowedOrigins.Add($"{_origin.Scheme}://{host}{(_origin.IsDefaultPort ? "" : $":{_origin.Port}")}");
        foreach (var origin in _allowedOrigins) _allowedHosts.Add(new Uri(origin).Authority);
    }

    public string DataDirectory { get; }
    public string TokenFile { get; }
    public bool UsesPassword => _ownerPassword is not null;
    public string CodexHome => Path.Combine(DataDirectory, "codex");
    public string Home => Path.Combine(DataDirectory, "home");
    public string WorkingDirectory => Path.Combine(DataDirectory, "workspace");

    public static Uri ValidateOrigin(string value, bool allowInsecureHttp = false)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? origin) || origin.UserInfo.Length != 0 ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            origin.Scheme is not ("http" or "https") || (!IsLoopback(origin) && origin.Scheme != "https" && !allowInsecureHttp))
            throw new ArgumentException("GOBLIN_PUBLIC_ORIGIN must be an HTTPS origin or a loopback HTTP origin, unless GOBLIN_ALLOW_INSECURE_HTTP is explicitly enabled.");
        return origin;
    }

    private static bool IsLoopback(Uri uri) => uri.Host is "localhost" or "127.0.0.1" or "[::1]";
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static async Task<Workspace> OpenAsync(string directory, string publicOrigin, string? passwordHashFile = null, bool allowInsecureHttp = false)
    {
        ValidateOrigin(publicOrigin, allowInsecureHttp);
        var data = Path.GetFullPath(directory);
        foreach (var path in new[] { data, Path.Combine(data, "codex"), Path.Combine(data, "home"), Path.Combine(data, "workspace") })
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
            else
            {
                Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        if (passwordHashFile is not null)
            return new Workspace(data, publicOrigin, null, await OwnerPassword.LoadAsync(passwordHashFile), allowInsecureHttp);

        var tokenFile = Path.Combine(data, "owner-token");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var stream = new FileStream(tokenFile, options);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(NewToken() + "\n"));
        }
        catch (IOException) when (File.Exists(tokenFile)) { }
        var token = (await File.ReadAllTextAsync(tokenFile)).Trim();
        if (token.Length is < 32 or > 256) throw new InvalidOperationException("The owner-token file is invalid.");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return new Workspace(data, publicOrigin, token, allowInsecureHttp: allowInsecureHttp);
    }

    public void ValidateRequest(HttpRequest request)
    {
        if (!_allowedHosts.Contains(request.Host.Value ?? ""))
            throw new PublicError("invalid_host", "Open the configured workspace address.", 403);
        if (HttpMethods.IsPost(request.Method) && !_allowedOrigins.Contains(request.Headers.Origin.ToString()))
            throw new PublicError("invalid_origin", "This request must come from the workspace page.", 403);
    }

    public string? SessionId(HttpRequest request)
    {
        var value = request.Cookies[CookieName];
        if (value is null || !SessionTokenPattern().IsMatch(value)) return null;
        var id = Convert.ToHexString(Hash(value));
        lock (_gate)
        {
            if (_sessions.TryGetValue(id, out DateTimeOffset expiry) && expiry > DateTimeOffset.UtcNow) return id;
            _sessions.Remove(id);
            return null;
        }
    }

    public SessionState Unlock(string? token, HttpResponse response)
    {
        lock (_gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            _failedUnlocks.RemoveAll(time => time <= now - TimeSpan.FromMinutes(1));
            if (_failedUnlocks.Count >= 5)
            {
                response.Headers.RetryAfter = "60";
                throw new PublicError("too_many_attempts", "Too many attempts. Wait a minute, then try again.", 429);
            }
            bool valid = token is not null && (_ownerPassword is not null
                ? _ownerPassword.Verify(token)
                : CryptographicOperations.FixedTimeEquals(Hash(token.Trim()), _ownerHash!));
            if (!valid)
            {
                _failedUnlocks.Add(now);
                throw new PublicError("invalid_workspace_code", UsesPassword
                    ? "The Goblin password is incorrect."
                    : "The workspace access code is incorrect.", 401);
            }
            _failedUnlocks.Clear();
            foreach (var id in _sessions.Where(x => x.Value <= now).Select(x => x.Key).ToArray()) _sessions.Remove(id);
            if (_sessions.Count >= 32) _sessions.RemoveAt(0);
            var session = NewToken();
            _sessions.Add(Convert.ToHexString(Hash(session)), now + SessionLifetime);
            SetCookie(response, session, SessionLifetime);
            return new(true, UsesPassword);
        }
    }

    public SessionState Lock(string id, HttpResponse response)
    {
        lock (_gate) _sessions.Remove(id);
        SetCookie(response, "", TimeSpan.Zero);
        return new(false, UsesPassword);
    }

    private void SetCookie(HttpResponse response, string value, TimeSpan lifetime) => response.Cookies.Append(CookieName, value, new CookieOptions
    {
        Path = "/",
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        MaxAge = lifetime,
        Secure = _origin.Scheme == "https"
    });

    [GeneratedRegex("^[\\w-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionTokenPattern();
}
