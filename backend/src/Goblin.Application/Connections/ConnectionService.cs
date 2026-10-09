using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts;

namespace Goblin.Application.Connections;

// Coordinates account changes with durable reservations and model discovery.
// Without durable Work, verification still uses the same integration contract.
public sealed class ConnectionService
{
    private readonly ICodexAuthentication _authentication;
    private readonly ConnectionStore? _store;
    private readonly ModelCatalogStore? _catalogs;

    public ConnectionService(ICodexAuthentication authentication) => _authentication = authentication;

    public ConnectionService(ICodexAuthentication authentication, ConnectionStore store, ModelCatalogStore catalogs)
        : this(authentication)
    {
        _store = store;
        _catalogs = catalogs;
    }

    public Task<AuthenticationState> StatusAsync(CancellationToken token) =>
        RunAsync(ConnectionObservationKind.Account, _authentication.StatusAsync, token);

    public Task<AuthenticationState> LoginChatGPTAsync(CancellationToken token) =>
        RunAsync(ConnectionObservationKind.ChangeCompletion, _authentication.LoginChatGPTAsync, token);

    public Task<AuthenticationState> LoginApiKeyAsync(string? key, CancellationToken token) =>
        RunAsync(ConnectionObservationKind.ChangeCompletion, () => _authentication.LoginApiKeyAsync(key), token);

    public Task<AuthenticationState> CancelLoginAsync(CancellationToken token) =>
        RunAsync(ConnectionObservationKind.ChangeCompletion, _authentication.CancelLoginAsync, token);

    public Task<AuthenticationState> LogoutAsync(CancellationToken token) =>
        RunAsync(ConnectionObservationKind.ChangeCompletion, _authentication.LogoutAsync, token);

    public async Task<PromptResult> PromptAsync(string? prompt, CancellationToken token)
    {
        if (_store is not null) await _store.BeginVerificationAsync(ConnectionStore.DefaultConnectionId, token);
        bool available = false;
        try
        {
            PromptResult result = await _authentication.SendPromptAsync(prompt, token);
            available = true;
            return result;
        }
        finally { if (_store is not null) await _store.EndVerificationAsync(ConnectionStore.DefaultConnectionId, available); }
    }

    public async Task<ConnectionView[]> ListAsync(CancellationToken token)
    {
        try { await StatusAsync(token); }
        catch (ConnectionFailure) { /* Persisted availability remains readable when the runtime is unavailable. */ }
        return await (_store ?? throw new InvalidOperationException("Durable Work is disabled.")).ListAsync(token);
    }

    private async Task<AuthenticationState> RunAsync(ConnectionObservationKind kind, Func<Task<AuthenticationState>> action,
        CancellationToken token)
    {
        bool changing = kind == ConnectionObservationKind.ChangeCompletion;
        if (changing && _store is not null) await _store.BeginChangeAsync(ConnectionStore.DefaultConnectionId);
        try
        {
            AuthenticationState state = await action();
            if (_store is not null)
            {
                bool available = state.Account is not null && state.RuntimeReady;
                ConnectionAvailability availability = available ? ConnectionAvailability.Available : ConnectionAvailability.Disconnected;
                string? signature = AccountSignature(state.Account);
                bool changed = changing
                    ? await _store.CompleteChangeAsync(ConnectionStore.DefaultConnectionId, availability, signature)
                    : await _store.ObserveAccountAsync(ConnectionStore.DefaultConnectionId, availability, signature);
                if (available)
                {
                    if (changed) _catalogs!.ScheduleRefresh(ConnectionStore.DefaultConnectionId);
                    else
                        try { await _catalogs!.ObserveExecutableAsync(ConnectionStore.DefaultConnectionId, token); }
                        catch { /* Discovery cannot change the connection result. */ }
                }
            }
            return state;
        }
        catch
        {
            if (_store is not null)
            {
                if (changing) await _store.CompleteChangeAsync(ConnectionStore.DefaultConnectionId, ConnectionAvailability.Unavailable, null);
                else await _store.ObserveAvailabilityAsync(ConnectionStore.DefaultConnectionId, ConnectionAvailability.Unavailable);
            }
            throw;
        }
    }

    private static string? AccountSignature(AccountView? account)
    {
        if (account is null) return null;
        string identity = account switch
        {
            ChatGPTAccountView chatGPT => "chatgpt:" + chatGPT.Email?.Trim().ToLowerInvariant(),
            ApiKeyAccountView => "apiKey",
            _ => account.GetType().Name
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}
