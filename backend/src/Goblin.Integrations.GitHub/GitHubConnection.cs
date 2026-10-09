using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;

namespace Goblin.Integrations.GitHub;

public sealed class GitHubFailure : Exception
{
    public GitHubFailure() : base("GitHub could not complete this operation. Check the connection and repository access, then try again.") { }
}

// A private CLI profile, never the host's credentials or configuration.
public sealed class GitHubConnection : IGitHubConnection, IDisposable
{
    private readonly string _directory;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _changes = new(1);
    private CancellationTokenSource? _cancellation;
    private Task? _login;
    private long _epoch;
    private GitHubState _state = new(true, null, null, null, null);

    public string Profile => Path.Combine(_directory, "active");
    internal GitHubCommandRunner Commands { get; }
    private string LegacyFile => Path.Combine(Path.GetDirectoryName(_directory)!, "github.json");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public GitHubConnection(string directory, string command = "gh")
    {
        _directory = Path.GetFullPath(directory);
        Commands = new(command, Profile);
        PrivateDirectory(_directory);
        string accountFile = Path.Combine(Profile, "account.json");
        if (File.Exists(accountFile))
        {
            try
            {
                GitRepositoryAccount account = JsonSerializer.Deserialize<GitRepositoryAccount>(File.ReadAllText(accountFile), Json)!;
                if (string.IsNullOrWhiteSpace(account.Generation) || string.IsNullOrWhiteSpace(account.AccountId)) throw new GitHubFailure();
                _state = new(true, account.Login, null, null, null, GitHubConnectionStatus.Connected, account);
            }
            catch { _state = new(true, null, null, null, "The saved GitHub connection could not be read. Sign in again to restore access.", GitHubConnectionStatus.Unavailable); }
        }
        else if (File.Exists(Path.Combine(Path.GetDirectoryName(_directory)!, "github.json")))
            _state = _state with { Notice = "Sign in once with GitHub CLI to replace the previous GitHub connection." };
    }

    public Task<GitHubState> StatusAsync() { lock (_gate) return Task.FromResult(_state); }

    public async Task<GitHubState> StartAsync()
    {
        await _changes.WaitAsync();
        try
        {
            lock (_gate)
            {
                if (_state.Account is not null || _state.Status == GitHubConnectionStatus.Connecting) throw new GitHubFailure();
                long epoch = ++_epoch;
                string staging = Path.Combine(_directory, "login-" + Guid.NewGuid().ToString("N"));
                PrivateDirectory(staging);
                _cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                _state = new(true, null, null, null, null, GitHubConnectionStatus.Connecting);
                _login = LoginAsync(epoch, staging, _cancellation.Token);
                return _state;
            }
        }
        finally { _changes.Release(); }
    }

    private async Task LoginAsync(long epoch, string staging, CancellationToken token)
    {
        try
        {
            await Commands.RunCliAsync(["auth", "login", "--hostname", "github.com", "--web", "--git-protocol", "https", "--skip-ssh-key", "--insecure-storage"], token, staging,
                line =>
                {
                    // gh has no JSON device-login output. Pin and test this narrow parser.
                    Match code = Regex.Match(line, @"First copy your one-time code: ([A-Z0-9]{4}-[A-Z0-9]{4})$");
                    lock (_gate) if (epoch == _epoch && code.Success)
                        _state = _state with { UserCode = code.Groups[1].Value, VerificationUrl = "https://github.com/login/device" };
                });
            GitHubUserResponse viewer = JsonSerializer.Deserialize<GitHubUserResponse>(await Commands.RunCliAsync(["api", "user"], token, staging)) ?? throw new GitHubFailure();
            var account = new GitRepositoryAccount(Guid.NewGuid().ToString("N"), viewer.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), viewer.Login);
            await File.WriteAllTextAsync(Path.Combine(staging, "account.json"), JsonSerializer.Serialize(account, Json), token);
            foreach (string file in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (epoch != _epoch) return;
                if (Directory.Exists(Profile)) Directory.Delete(Profile, true);
                Directory.Move(staging, Profile);
                File.Delete(LegacyFile);
                _state = new(true, account.Login, null, null, null, GitHubConnectionStatus.Connected, account);
            }
        }
        catch
        {
            lock (_gate) if (epoch == _epoch) _state = new(true, null, null, null,
                token.IsCancellationRequested ? "Sign-in expired or was cancelled. Start again for a new code." : "GitHub sign-in failed. Check that GitHub CLI is installed and try again.", GitHubConnectionStatus.Disconnected);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    public async Task<GitHubState> DisconnectAsync()
    {
        await _changes.WaitAsync();
        try
        {
            Task? login;
            lock (_gate) { ++_epoch; _cancellation?.Cancel(); login = _login; }
            if (login is not null) await login;
            lock (_gate)
            {
                if (Directory.Exists(Profile)) Directory.Delete(Profile, true);
                File.Delete(LegacyFile);
                _state = new(true, null, null, null, "Goblin’s saved access was removed. GitHub authorizations can also be revoked in your GitHub account settings.");
                return _state;
            }
        }
        finally { _changes.Release(); }
    }

    public async Task<GitHubState> CheckAsync(CancellationToken token = default)
    {
        GitHubState before = await StatusAsync();
        if (before.Account is null) return before;
        try
        {
            GitHubUserResponse viewer = JsonSerializer.Deserialize<GitHubUserResponse>(await Commands.RunCliAsync(["api", "user"], token)) ?? throw new GitHubFailure();
            if (viewer.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) != before.Account.AccountId) throw new GitHubFailure();
            lock (_gate) if (_state.Account?.Generation == before.Account.Generation) _state = _state with { Status = GitHubConnectionStatus.Connected, Notice = null };
        }
        catch
        {
            lock (_gate) if (_state.Account?.Generation == before.Account.Generation)
                _state = _state with { Status = GitHubConnectionStatus.Unavailable, Notice = "GitHub access could not be verified. Check the connection or disconnect and sign in again." };
        }
        return await StatusAsync();
    }

    public async Task<GitRepositoryAccount?> GetAccountAsync(CancellationToken token) => (await StatusAsync()).Account;

    public async Task<GitRepositoryInfo[]> GitRepositoriesAsync(int page, CancellationToken token)
    {
        if (page is < 1 or > 1000) throw new GitHubFailure();
        GitHubRepositoryResponse[] result = JsonSerializer.Deserialize<GitHubRepositoryResponse[]>(await Commands.RunCliAsync(["api", $"user/repos?per_page=100&page={page}&sort=full_name"], token)) ?? throw new GitHubFailure();
        return [.. result.Select(GitRepository)];
    }

    public async Task<GitRepositoryInfo> GitRepositoryAsync(string name, CancellationToken token)
    {
        _ = new Goblin.Core.Work.GitRepositoryChange(name, "Goblin", "goblin@example.invalid");
        GitHubRepositoryResponse result = JsonSerializer.Deserialize<GitHubRepositoryResponse>(await Commands.RunCliAsync(["api", "repos/" + name], token)) ?? throw new GitHubFailure();
        return GitRepository(result);
    }

    private static GitRepositoryInfo GitRepository(GitHubRepositoryResponse row) =>
        new(row.Id, row.FullName, row.DefaultBranch, row.Permissions?.Push == true);

    public Task<string> CliAsync(string[] arguments, CancellationToken token, string? profile = null) =>
        Commands.RunCliAsync(arguments, token, profile);

    public Task<string> GitAsync(string directory, string[] arguments, CancellationToken token) =>
        Commands.RunGitAsync(directory, arguments, token);

    public static void PrivateDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose() { _cancellation?.Cancel(); }
}
