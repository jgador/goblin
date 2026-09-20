using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Goblin.Integrations.GitHub;

public sealed record GitHubState(bool Configured, string? Login, string? UserCode, string? VerificationUrl, string? Notice);
public sealed class GitHubFailure() : Exception("GitHub connection could not be updated. Check the connection settings and try again.");
public sealed record GitHubCredentials(string AccessToken, string Login);

public sealed class GitHubConnection(string? clientId, string credentialFile) : IDisposable
{
    private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _deviceCode, _userCode;
    private DateTimeOffset _expires, _nextPoll;
    private int _interval;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<GitHubState> StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (string.IsNullOrWhiteSpace(clientId)) return new(false, null, null, null, "Configure a GitHub OAuth application's client ID with device flow enabled.");
            if (File.Exists(credentialFile) || _deviceCode is not null) throw new GitHubFailure();
            using JsonDocument response = await PostAsync("https://github.com/login/device/code", new() { ["client_id"] = clientId, ["scope"] = "repo read:user" });
            _deviceCode = response.RootElement.GetProperty("device_code").GetString();
            _userCode = response.RootElement.GetProperty("user_code").GetString();
            if (response.RootElement.GetProperty("verification_uri").GetString() != "https://github.com/login/device") throw new GitHubFailure();
            _interval = Math.Max(5, response.RootElement.GetProperty("interval").GetInt32());
            _expires = DateTimeOffset.UtcNow.AddSeconds(response.RootElement.GetProperty("expires_in").GetInt32());
            _nextPoll = DateTimeOffset.UtcNow.AddSeconds(_interval);
            return new(true, null, _userCode, "https://github.com/login/device", null);
        }
        catch { throw new GitHubFailure(); }
        finally { _gate.Release(); }
    }

    public async Task<GitHubState> StatusAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (File.Exists(credentialFile))
            {
                GitHubCredentials credentials = JsonSerializer.Deserialize<GitHubCredentials>(await File.ReadAllTextAsync(credentialFile), Json)!;
                return new(!string.IsNullOrWhiteSpace(clientId), credentials.Login, null, null, null);
            }
            if (_deviceCode is null) return new(!string.IsNullOrWhiteSpace(clientId), null, null, null, null);
            if (_expires < DateTimeOffset.UtcNow) { _deviceCode = null; return new(true, null, null, null, "Sign-in expired. Start again."); }
            if (_nextPoll <= DateTimeOffset.UtcNow)
            {
                _nextPoll = DateTimeOffset.UtcNow.AddSeconds(_interval);
                using JsonDocument response = await PostAsync("https://github.com/login/oauth/access_token", new()
                {
                    ["client_id"] = clientId!,
                    ["device_code"] = _deviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                });
                if (response.RootElement.TryGetProperty("access_token", out JsonElement access))
                {
                    string token = access.GetString()!;
                    using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.UserAgent.ParseAdd("Goblin/0.1");
                    using HttpResponseMessage account = await _client.SendAsync(request);
                    if (!account.IsSuccessStatusCode) throw new GitHubFailure();
                    using JsonDocument user = JsonDocument.Parse(await account.Content.ReadAsStringAsync());
                    string login = user.RootElement.GetProperty("login").GetString()!;
                    Directory.CreateDirectory(Path.GetDirectoryName(credentialFile)!);
                    string temporary = credentialFile + "." + Guid.NewGuid().ToString("N");
                    await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                        await JsonSerializer.SerializeAsync(file, new GitHubCredentials(token, login), Json);
                        file.Flush(true);
                    }
                    File.Move(temporary, credentialFile, overwrite: true);
                    _deviceCode = null;
                    return new(true, login, null, null, null);
                }
                string error = response.RootElement.GetProperty("error").GetString()!;
                if (error == "slow_down") { _interval += 5; _nextPoll = DateTimeOffset.UtcNow.AddSeconds(_interval); }
                else if (error != "authorization_pending") { _deviceCode = null; return new(true, null, null, null, "Sign-in did not finish. Start again."); }
            }
            return new(true, null, _userCode, "https://github.com/login/device", null);
        }
        catch { throw new GitHubFailure(); }
        finally { _gate.Release(); }
    }
    public async Task<GitHubState> DisconnectAsync()
    {
        await _gate.WaitAsync();
        try { _deviceCode = null; File.Delete(credentialFile); return new(!string.IsNullOrWhiteSpace(clientId), null, null, null, null); }
        finally { _gate.Release(); }
    }
    private async Task<JsonDocument> PostAsync(string url, Dictionary<string, string> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage response = await _client.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new GitHubFailure();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
    public void Dispose() { _client.Dispose(); _gate.Dispose(); }
}
