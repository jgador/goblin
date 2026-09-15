using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Protocol;
using Goblin.Web.Codex;

namespace Goblin.Web;

public sealed class Authentication : IDisposable
{
    private readonly CodexClient _codex;
    private readonly Func<string, Task<string>> _verifyApiKey;
    private readonly PromptRunner _prompts;
    private readonly Lock _gate = new();
    private Task _queue = Task.CompletedTask;
    private AccountView? _account;
    private DeviceLogin? _login;
    private Notice? _notice;
    private string? _verification;
    private int _promptPending;

    public Authentication(CodexClient codex, Func<string, Task<string>> verifyApiKey, TimeSpan promptTimeout)
    {
        _codex = codex;
        _verifyApiKey = verifyApiKey;
        _prompts = new PromptRunner(codex, promptTimeout);
        codex.Notification += OnNotification;
        codex.Disconnected += OnDisconnected;
    }

    // Queue callbacks instead of awaiting them on the stdout reader: callbacks make RPC requests.
    private void OnNotification(ServerNotification notification)
    {
        if (notification is AccountLoginCompletedServerNotification completed)
            _ = Serial(async () =>
            {
                lock (_gate)
                {
                    if (_login is null || completed.Params.LoginId != _login.Id) return false;
                    _login = null;
                    _notice = completed.Params.Success ? null : new("error",
                        "Sign-in did not finish. The code may have expired or device-code login may be disabled. Please try again.");
                }
                await RefreshAsync();
                return true;
            });
        else if (notification is AccountUpdatedServerNotification)
            _ = Serial(async () => { await RefreshAsync(); return true; });
    }

    private void OnDisconnected()
    {
        lock (_gate)
        {
            if (_login is not null) _notice = new("error", "Codex restarted during sign-in. Please start sign-in again.");
            _login = null;
            _account = null;
            _verification = null;
        }
    }

    private Task<T> Serial<T>(Func<Task<T>> operation)
    {
        lock (_gate)
        {
            Task previous = _queue;
            async Task<T> Run()
            {
                await Task.Yield();
                await previous;
                return await operation();
            }
            Task<T> result = Run();
            _queue = ObserveAsync(result);
            return result;
        }
    }

    private static async Task ObserveAsync(Task task) { try { await task; } catch { } }

    private async Task RefreshAsync()
    {
        await _codex.StartAsync();
        GetAccountResponse result = await _codex.RequestAsync<GetAccountParams, GetAccountResponse>("account/read", new() { RefreshToken = false });
        if (!result.RequiresOpenaiAuth)
            throw new PublicError("unexpected_provider", "Goblin requires Codex's OpenAI provider. Check the runtime configuration.", 503);
        lock (_gate)
        {
            _account = result.Account switch
            {
                ChatgptAccount account => new ChatgptAccountView(account.Email, account.PlanType),
                ApiKeyAccount => new ApiKeyAccountView(),
                null => null,
                _ => throw PublicError.RuntimeUnavailable()
            };
            if (_account is not null) _login = null;
        }
    }

    private AuthenticationState Snapshot()
    {
        lock (_gate) return new(_account, _login, _notice, _verification, _codex.Ready);
    }

    public Task<AuthenticationState> StatusAsync() => Serial(async () => { await RefreshAsync(); return Snapshot(); });

    public Task<PromptResult> SendPromptAsync(string? value, CancellationToken cancellationToken = default)
    {
        var prompt = value?.Trim() ?? "";
        if (prompt.Length is < 1 or > 500 || prompt.Any(c => c is <= '\x08' or '\x0b' or '\x0c' or >= '\x0e' and <= '\x1f'))
            throw new PublicError("invalid_prompt", "Enter a short prompt of 1–500 characters.");
        if (Interlocked.CompareExchange(ref _promptPending, 1, 0) != 0)
            throw new PublicError("prompt_in_progress", "A prompt is already running. Wait for it to finish.", 409);
        return Run();
        async Task<PromptResult> Run()
        {
            try
            {
                return await Serial(async () =>
                {
                    await RefreshAsync();
                    string authType;
                    lock (_gate) authType = _account switch
                    {
                        ApiKeyAccountView => "apiKey",
                        ChatgptAccountView => "chatgpt",
                        _ => throw new PublicError("not_connected", "Connect a ChatGPT account or API key first.", 409)
                    };
                    (string Reply, string Model, long DurationMs) result = await _prompts.RunAsync(prompt, cancellationToken);
                    lock (_gate)
                    {
                        if (authType == "apiKey") _verification = "accepted";
                        _notice = null;
                    }
                    return new PromptResult(result.Reply, result.Model, result.DurationMs, authType);
                });
            }
            finally { Interlocked.Exchange(ref _promptPending, 0); }
        }
    }

    private async Task RequireDisconnectedAsync()
    {
        await RefreshAsync();
        lock (_gate)
        {
            if (_account is not null) throw new PublicError("already_connected", "Disconnect the current account before switching sign-in methods.", 409);
            if (_login is not null) throw new PublicError("login_in_progress", "Sign-in is already in progress. Finish or cancel it first.", 409);
        }
    }

    public Task<AuthenticationState> LoginChatGptAsync() => Serial(async () =>
    {
        await RequireDisconnectedAsync();
        lock (_gate) _notice = null;
        LoginAccountResponse result = await _codex.RequestAsync<LoginAccountParams, LoginAccountResponse>("account/login/start", new ChatgptDeviceCodeLoginAccountParams());
        if (result is not ChatgptDeviceCodeLoginAccountResponse device ||
            string.IsNullOrEmpty(device.UserCode) || device.UserCode.Length > 64 ||
            !Uri.TryCreate(device.VerificationUrl, UriKind.Absolute, out Uri? url) ||
            url.Scheme != "https" || url.Host != "auth.openai.com" || !url.IsDefaultPort ||
            url.AbsolutePath != "/codex/device" || url.UserInfo.Length != 0)
        {
            if (result is ChatgptDeviceCodeLoginAccountResponse invalid)
                try { await _codex.RequestAsync<CancelLoginAccountParams, CancelLoginAccountResponse>("account/login/cancel", new() { LoginId = invalid.LoginId }); } catch { }
            throw new PublicError("unexpected_login_response", "Codex returned an unexpected sign-in response. Check the pinned Codex version.", 502);
        }
        lock (_gate) _login = new(device.LoginId, url.AbsoluteUri, device.UserCode);
        return Snapshot();
    });

    public Task<AuthenticationState> LoginApiKeyAsync(string? value) => Serial(async () =>
    {
        var apiKey = value?.Trim() ?? "";
        if (apiKey.Length is < 20 or > 4096 || apiKey.Any(c => c is < '\x21' or > '\x7e'))
            throw new PublicError("invalid_api_key", "Enter a complete OpenAI API key without spaces.");
        await RequireDisconnectedAsync();
        var verification = await _verifyApiKey(apiKey);
        await _codex.RequestAsync<LoginAccountParams, LoginAccountResponse>("account/login/start", new ApiKeyLoginAccountParams { ApiKey = apiKey });
        lock (_gate)
        {
            _verification = verification;
            _notice = verification == "unverified" ? new("info",
                "Key saved. Its permissions or rate limits prevented verification. Model access has not been tested.") : null;
        }
        await RefreshAsync();
        return Snapshot();
    });

    public Task<AuthenticationState> CancelLoginAsync() => Serial(async () =>
    {
        DeviceLogin? login;
        lock (_gate) login = _login;
        if (login is not null)
        {
            await _codex.RequestAsync<CancelLoginAccountParams, CancelLoginAccountResponse>("account/login/cancel", new() { LoginId = login.Id });
            lock (_gate) _login = null;
        }
        lock (_gate) _notice = null;
        await RefreshAsync();
        return Snapshot();
    });

    public Task<AuthenticationState> LogoutAsync() => Serial(async () =>
    {
        await _codex.StartAsync();
        DeviceLogin? login;
        lock (_gate) login = _login;
        if (login is not null)
        {
            await _codex.RequestAsync<CancelLoginAccountParams, CancelLoginAccountResponse>("account/login/cancel", new() { LoginId = login.Id });
            lock (_gate) _login = null;
        }
        await _codex.RequestAsync<LogoutAccountResponse>("account/logout");
        lock (_gate) { _verification = null; _notice = null; }
        await RefreshAsync();
        return Snapshot();
    });

    public void Dispose()
    {
        _codex.Notification -= OnNotification;
        _codex.Disconnected -= OnDisconnected;
    }
}
