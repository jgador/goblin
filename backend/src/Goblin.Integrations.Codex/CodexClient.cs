using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Protocol;

namespace Goblin.Integrations.Codex;

/// <summary>Owns a connection to the official Rust app-server; no conversation state lives here.</summary>
public sealed partial class CodexClient : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<RequestId, Pending> _pending = new();
    private Process? _process;
    private Task? _starting;
    private Task _retiring = Task.CompletedTask;
    private long _nextId;
    private bool _closed;
    private bool _ready;

    public CodexClient(CodexOptions options) => Options = options;

    public CodexOptions Options { get; }
    public bool Ready { get { lock (_gate) return _ready; } }
    public string? StartedExecutableStamp
    {
        get { lock (_gate) return field; }

        private set;
    }
    public event Action<ServerNotification>? Notification;
    public event Action? Disconnected;

    private sealed record Pending(string Method, TaskCompletionSource<JsonElement> Completion);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Task start;
        lock (_gate)
        {
            if (_closed) throw IntegrationFailure.RuntimeUnavailable();
            if (_ready) return Task.CompletedTask;
            if (_starting is null || _starting.IsCompleted) _starting = StartCoreAsync();
            start = _starting;
        }
        return start.WaitAsync(cancellationToken);
    }

    private async Task StartCoreAsync()
    {
        // Do not acquire the credential directory until the predecessor has exited.
        await Task.Yield();
        await _retiring;
        Process child;
        lock (_gate)
        {
            if (_closed) throw IntegrationFailure.RuntimeUnavailable();
            child = new Process { StartInfo = Options.CreateStartInfo() };
            try
            {
                string stamp = Options.ExecutableStamp();
                if (!child.Start()) throw IntegrationFailure.RuntimeUnavailable();
                _process = child;
                StartedExecutableStamp = stamp;
            }
            catch { child.Dispose(); throw IntegrationFailure.RuntimeUnavailable(); }
        }
        _ = ReadAsync(child);
        _ = DrainErrorsAsync(child);
        try
        {
            await RequestAsync<InitializeParams, InitializeResponse>("initialize", new()
            {
                ClientInfo = new ClientInfo { Name = "goblin_auth", Title = "Goblin", Version = "0.1.0" }
            });
            await SendAsync(child, new JSONRPCNotification
            {
                Method = "initialized",
                Params = JsonSerializer.SerializeToElement(new Dictionary<string, string>())
            });
            lock (_gate)
            {
                if (_process != child || _closed || child.HasExited) throw IntegrationFailure.RuntimeUnavailable();
                _ready = true;
            }
        }
        catch { Fail(child); throw; }
    }

    public async Task<TResult> RequestAsync<TParams, TResult>(string method, TParams parameters,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = new RequestId(Interlocked.Increment(ref _nextId));
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        Process child;
        lock (_gate)
        {
            child = _process ?? throw IntegrationFailure.RuntimeUnavailable();
            if (_closed) throw IntegrationFailure.RuntimeUnavailable();
            _pending[id] = new Pending(method, completion);
        }
        using var timeout = new CancellationTokenSource(Options.RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            await SendAsync(child, new JSONRPCRequest
            {
                Id = id,
                Method = method,
                Params = JsonSerializer.SerializeToElement(parameters, ProtocolJson.Options)
            }, linked.Token);
            JsonElement result = await completion.Task.WaitAsync(linked.Token);
            return result.Deserialize<TResult>(ProtocolJson.Options) ?? throw new JsonException();
        }
        catch (OperationCanceledException)
        {
            // A request may already have mutated Codex. Retire it before reusing credentials.
            _pending.TryRemove(id, out _);
            Fail(child);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new IntegrationFailure("runtime_timeout", "Codex took too long to respond. Please retry.");
        }
        catch (JsonException) { Fail(child); throw IntegrationFailure.RuntimeUnavailable(); }
        catch (IntegrationFailure) { throw; }
        catch { Fail(child); throw IntegrationFailure.RuntimeUnavailable(); }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task<TResult> RequestAsync<TResult>(string method, CancellationToken cancellationToken = default) =>
        RequestAsync<Dictionary<string, string>, TResult>(method, [], cancellationToken);

    private async Task SendAsync<T>(Process child, T message, CancellationToken cancellationToken = default)
    {
        string line = JsonSerializer.Serialize(message, ProtocolJson.Options);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
                if (_process != child || _closed) throw IntegrationFailure.RuntimeUnavailable();
            await child.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
            await child.StandardInput.FlushAsync(cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    private async Task ReadAsync(Process child)
    {
        try
        {
            await foreach (string line in JsonLines.ReadAsync(child.StandardOutput))
            {
                lock (_gate) if (_process != child) return;
                (bool hasMethod, bool hasId, bool hasError) = InspectEnvelope(line);
                if (hasMethod)
                {
                    if (hasId)
                    {
                        JSONRPCRequest request = Deserialize<JSONRPCRequest>(line);
                        await SendAsync(child, new JSONRPCError
                        {
                            Id = request.Id,
                            Error = new JSONRPCErrorError { Code = -32601, Message = "Interactive tools are disabled." }
                        });
                    }
                    else
                    {
                        JSONRPCNotification envelope = Deserialize<JSONRPCNotification>(line);
                        if (ServerNotification.IsKnownMethod(envelope.Method))
                            Notification?.Invoke(Deserialize<ServerNotification>(line));
                    }
                }
                else if (hasError)
                {
                    JSONRPCError error = Deserialize<JSONRPCError>(line);
                    if (_pending.TryRemove(error.Id, out Pending? request))
                        request.Completion.TrySetException(new IntegrationFailure("codex_request_failed",
                            request.Method == "account/login/start"
                                ? "Codex could not complete this request. For ChatGPT, check that device-code login is enabled, then retry."
                                : "Codex could not complete this request. Please retry."));
                }
                else if (hasId)
                {
                    JSONRPCResponse response = Deserialize<JSONRPCResponse>(line);
                    if (_pending.TryRemove(response.Id, out Pending? request)) request.Completion.TrySetResult(response.Result);
                }
                else throw new JsonException();
            }
        }
        catch { /* Transport and protocol details can include credentials. */ }
        finally { Fail(child); }
    }

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, ProtocolJson.Options) ?? throw new JsonException();

    // Only classifies the wire envelope. All data is decoded into generated schema models.
    private static (bool Method, bool Id, bool Error) InspectEnvelope(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
        bool method = false, id = false, error = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            method |= reader.ValueTextEquals("method");
            id |= reader.ValueTextEquals("id");
            error |= reader.ValueTextEquals("error");
            if (!reader.Read()) throw new JsonException();
            reader.Skip();
        }
        if (reader.Read()) throw new JsonException();
        return (method, id, error);
    }

    private static async Task DrainErrorsAsync(Process child)
    {
        try
        {
            char[] buffer = new char[4096];
            while (await child.StandardError.ReadAsync(buffer) > 0) { }
        }
        catch { }
    }

    public void Fail()
    {
        Process? child;
        lock (_gate) child = _process;
        if (child is not null) Fail(child);
    }

    private void Fail(Process child)
    {
        lock (_gate)
        {
            if (_process != child) return;
            _process = null;
            _ready = false;
            StartedExecutableStamp = null;
            _retiring = RetireAsync(child);
            foreach ((RequestId id, Pending? request) in _pending)
                if (_pending.TryRemove(id, out _)) request.Completion.TrySetException(IntegrationFailure.RuntimeUnavailable());
        }
        Disconnected?.Invoke();
    }

    private async Task RetireAsync(Process child)
    {
        try
        {
            if (!child.HasExited)
            {
                if (!OperatingSystem.IsWindows()) Kill(child.Id, 15);
                else child.StandardInput.Close();
                try { await child.WaitForExitAsync().WaitAsync(Options.ShutdownTimeout); }
                catch (TimeoutException)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) when (child.HasExited) { }
        finally { child.Dispose(); }
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);

    public async ValueTask DisposeAsync()
    {
        Task? starting;
        lock (_gate) { _closed = true; starting = _starting; }
        Fail();
        if (starting is not null) try { await starting; } catch { }
        await _retiring;
    }
}
