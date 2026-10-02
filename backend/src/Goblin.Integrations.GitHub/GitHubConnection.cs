using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;
using Env = Goblin.Contracts.Configuration.EnvironmentVariables;

namespace Goblin.Integrations.GitHub;

public sealed record GitHubState(bool Configured, string? Login, string? UserCode,
    string? VerificationUrl, string? Notice, GitHubConnectionStatus Status = GitHubConnectionStatus.Disconnected, RepositoryAccount? Account = null);

public sealed class GitHubFailure : Exception
{
    public GitHubFailure() : base("GitHub could not complete this operation. Check the connection and repository access, then try again.") { }
}

// A private CLI profile, never the host's credentials or configuration.
public sealed class GitHubConnection : IRepositoryCatalog, IDisposable
{
    private readonly string _directory;
    private readonly string _command;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _changes = new(1);
    private CancellationTokenSource? _cancellation;
    private Task? _login;
    private long _epoch;
    private GitHubState _state = new(true, null, null, null, null);

    public string Profile => Path.Combine(_directory, "active");
    private string LegacyFile => Path.Combine(Path.GetDirectoryName(_directory)!, "github.json");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public GitHubConnection(string directory, string command = "gh")
    {
        _directory = Path.GetFullPath(directory);
        _command = command;
        PrivateDirectory(_directory);
        string accountFile = Path.Combine(Profile, "account.json");
        if (File.Exists(accountFile))
        {
            try
            {
                RepositoryAccount account = JsonSerializer.Deserialize<RepositoryAccount>(File.ReadAllText(accountFile), Json)!;
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
            await RunAsync(_command, ["auth", "login", "--hostname", "github.com", "--web", "--git-protocol", "https", "--skip-ssh-key", "--insecure-storage"], staging, token,
                line =>
                {
                    // gh has no JSON device-login output. Pin and test this narrow parser.
                    Match code = Regex.Match(line, @"First copy your one-time code: ([A-Z0-9]{4}-[A-Z0-9]{4})$");
                    lock (_gate) if (epoch == _epoch && code.Success)
                        _state = _state with { UserCode = code.Groups[1].Value, VerificationUrl = "https://github.com/login/device" };
                });
            GitHubUserResponse viewer = JsonSerializer.Deserialize<GitHubUserResponse>(await CliAsync(["api", "user"], token, staging)) ?? throw new GitHubFailure();
            var account = new RepositoryAccount(Guid.NewGuid().ToString("N"), viewer.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), viewer.Login);
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
            GitHubUserResponse viewer = JsonSerializer.Deserialize<GitHubUserResponse>(await CliAsync(["api", "user"], token)) ?? throw new GitHubFailure();
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

    public async Task<RepositoryAccount?> GetAccountAsync(CancellationToken token) => (await StatusAsync()).Account;

    public async Task<RepositoryInfo[]> RepositoriesAsync(int page, CancellationToken token)
    {
        if (page is < 1 or > 1000) throw new GitHubFailure();
        GitHubRepositoryResponse[] result = JsonSerializer.Deserialize<GitHubRepositoryResponse[]>(await CliAsync(["api", $"user/repos?per_page=100&page={page}&sort=full_name"], token)) ?? throw new GitHubFailure();
        return [.. result.Select(Repository)];
    }

    public async Task<RepositoryInfo> RepositoryAsync(string name, CancellationToken token)
    {
        _ = new Goblin.Core.Work.RepositoryChange(name, "Goblin", "goblin@example.invalid");
        GitHubRepositoryResponse result = JsonSerializer.Deserialize<GitHubRepositoryResponse>(await CliAsync(["api", "repos/" + name], token)) ?? throw new GitHubFailure();
        return Repository(result);
    }

    private static RepositoryInfo Repository(GitHubRepositoryResponse row) =>
        new(row.Id, row.FullName, row.DefaultBranch, row.Permissions?.Push == true);

    public Task<string> CliAsync(string[] arguments, CancellationToken token, string? profile = null) => RunAsync(_command, arguments, profile ?? Profile, token);

    public Task<string> GitAsync(string directory, string[] arguments, CancellationToken token) =>
        RunAsync("git", ["-c", "core.hooksPath=/dev/null", "-c", "credential.helper=", "-c", "credential.helper=!gh auth git-credential", .. arguments], Profile, token, workingDirectory: directory);

    private static async Task<string> RunAsync(string command, string[] arguments, string profile, CancellationToken token,
        Action<string>? progress = null, string? workingDirectory = null)
    {
        bool watchdog = OperatingSystem.IsLinux();
        var info = new ProcessStartInfo(watchdog ? "/usr/bin/timeout" : command) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = workingDirectory ?? profile };
        if (watchdog) foreach (string prefix in new[] { "--kill-after=5s", progress is null ? "120s" : "900s", command }) info.ArgumentList.Add(prefix);
        info.Environment.Clear();
        foreach ((string, string) pair in new[] { (Env.Path, Environment.GetEnvironmentVariable(Env.Path) ?? "/usr/bin:/bin"), (Env.Home, profile), (Env.GhConfigDir, profile),
            (Env.GhPromptDisabled, "1"), (Env.GhNoUpdateNotifier, "1"), (Env.NoColor, "1"), (Env.LcAll, "C"),
            (Env.GitTerminalPrompt, "0"), (Env.GitConfigNosystem, "1"), (Env.GitConfigGlobal, "/dev/null") }) info.Environment[pair.Item1] = pair.Item2;
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(progress is null ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(15));
        using Process process = Process.Start(info) ?? throw new GitHubFailure();
        process.StandardInput.Close();
        Task<string> output = ReadAsync(process.StandardOutput, null), errors = ReadAsync(process.StandardError, progress);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { try { process.Kill(true); } catch (InvalidOperationException) { } await process.WaitForExitAsync(); throw new GitHubFailure(); }
        string result = await output;
        await errors;
        if (process.ExitCode != 0) throw new GitHubFailure();
        return result;
    }

    private static async Task<string> ReadAsync(StreamReader reader, Action<string>? progress)
    {
        var text = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            progress?.Invoke(line);
            if (text.Length < 4 * 1024 * 1024) text.AppendLine(line);
        }
        return text.ToString();
    }

    public static void PrivateDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose() { _cancellation?.Cancel(); }
}
