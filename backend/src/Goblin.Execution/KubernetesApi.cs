using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Execution.Kubernetes;

namespace Goblin.Execution;

// Namespace-scoped control plane access. The service account is mounted only
// in Goblin, never in an agent environment. Upstream bodies are never errors.
public sealed class KubernetesApi : IDisposable
{
    private readonly HttpClient _client;
    private readonly string? _tokenFile;
    public KubernetesApi(string? address = null, string? tokenFile = null, string? caFile = null)
    {
        address ??= "https://kubernetes.default.svc";
        _tokenFile = tokenFile;
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (caFile is not null)
        {
            X509Certificate2 root = X509CertificateLoader.LoadCertificateFromFile(caFile);
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(root);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(certificate);
            };
        }
        _client = new(handler) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<JsonObject?> GetAsync(string path, CancellationToken token)
    {
        string? text = await SendAsync(HttpMethod.Get, path, null, token, allowMissing: true);
        return text is null ? null : JsonNode.Parse(text)!.AsObject();
    }
    public async Task<T?> GetAsync<T>(string path, CancellationToken token) where T : class
    {
        string? text = await SendAsync(HttpMethod.Get, path, null, token, allowMissing: true);
        return text is null ? null : JsonSerializer.Deserialize<T>(text, KubernetesJson.Options)
            ?? throw new IOException("Execution control plane returned an empty resource.");
    }
    public Task<string?> LogsAsync(string path, CancellationToken token) => SendAsync(HttpMethod.Get, path, null, token, allowMissing: true);
    public async Task<bool> CreateAsync<T>(string path, T body, CancellationToken token) where T : class =>
        await SendAsync(HttpMethod.Post, path, JsonSerializer.Serialize(body, KubernetesJson.Options), token, allowConflict: true) is not null;
    public Task<string?> PatchAsync<T>(string path, T body, CancellationToken token) where T : class =>
        SendAsync(HttpMethod.Patch, path, JsonSerializer.Serialize(body, KubernetesJson.Options), token);
    public async Task<bool> TryPatchAsync<T>(string path, T body, CancellationToken token) where T : class =>
        await SendAsync(HttpMethod.Patch, path, JsonSerializer.Serialize(body, KubernetesJson.Options), token, allowConflict: true) is not null;
    public Task<string?> DeleteAsync(string path, CancellationToken token) => SendAsync(HttpMethod.Delete, path,
        JsonSerializer.Serialize(new DeleteOptions { PropagationPolicy = "Foreground" }, KubernetesJson.Options), token, allowMissing: true);
    private async Task<string?> SendAsync(HttpMethod method, string path, string? body,
        CancellationToken token, bool allowMissing = false, bool allowConflict = false)
    {
        using var request = new HttpRequestMessage(method, path);
        if (_tokenFile is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await File.ReadAllTextAsync(_tokenFile, token)).Trim());
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8,
            method == HttpMethod.Patch ? "application/merge-patch+json" : "application/json");
        using HttpResponseMessage response = await _client.SendAsync(request, token);
        if ((allowMissing && response.StatusCode == HttpStatusCode.NotFound) ||
            (allowConflict && response.StatusCode == HttpStatusCode.Conflict)) return null;
        if (!response.IsSuccessStatusCode) throw new IOException("Execution control plane request failed.");
        return await response.Content.ReadAsStringAsync(token);
    }
    public async Task<ClientWebSocket> ExecAsync(string ns, string pod, string container, string[] command, bool tty, CancellationToken token)
    {
        // Callers supply only a recorded inspection pod, never browser-selected K8s paths.
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("v4.channel.k8s.io");
        if (_tokenFile is not null) socket.Options.SetRequestHeader("Authorization", "Bearer " + (await File.ReadAllTextAsync(_tokenFile, token)).Trim());
        string query = "container=" + Uri.EscapeDataString(container) + "&stdin=true&stdout=true&stderr=" + (!tty).ToString().ToLowerInvariant() + "&tty=" + tty.ToString().ToLowerInvariant();
        foreach (string argument in command) query += "&command=" + Uri.EscapeDataString(argument);
        var uri = new UriBuilder(new Uri(_client.BaseAddress!, "/api/v1/namespaces/" + ns + "/pods/" + pod + "/exec?" + query));
        uri.Scheme = uri.Scheme == "https" ? "wss" : "ws";
        try { await socket.ConnectAsync(uri.Uri, _client, token); return socket; }
        catch { socket.Dispose(); throw; }
    }
    public void Dispose() => _client.Dispose();
}
