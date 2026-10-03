using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Conversations;
using Env = Goblin.Contracts.Configuration.EnvironmentVariables;

namespace Goblin.Integrations.Slack;

// Slack's supported CLI runs only in a private, disposable setup directory.
// Runtime operation uses the Web API and Socket Mode directly, never this helper.
public sealed class SlackSetup : IAsyncDisposable
{
    public const string Version = "4.8.0";
    public const string LinuxX64Sha256 = "533ebc242561a79c6aaf238c3417ce113d1257ace80cf90f1e5f852d8ec9ca7b";
    public const string LinuxArm64Sha256 = "dbfc62385ac35d66aa3356d2a99ee1444bd05eafb7b14ef08235e408a2fae6cb";
    private readonly string _directory;
    private readonly SlackApi _api;
    private readonly SlackConnection _connection;

    private readonly Lock _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task _operation = Task.CompletedTask;
    private Task _expiry = Task.CompletedTask;
    private CancellationTokenSource? _expiryStop;

    private readonly SemaphoreSlim _cleanup = new(1);
    private string? _session, _ticket, _team;

    private SlackSetupView _view = new(SlackSetupStatus.Idle, null, null, null, null);

    private string Active => Path.Combine(_directory, "active");

    private string Profile => Path.Combine(Active, "profile");

    private string Project => Path.Combine(Active, "project");

    private string Executable => Path.Combine(Active, "slack");

    private string RecoveryPath => Path.Combine(_directory, "recovery.json");

    public SlackSetup(string directory, SlackApi api, SlackConnection connection)
    {
        _directory = Path.GetFullPath(directory); _api = api; _connection = connection;
        SlackCredentialStore.PrivateDirectory(_directory);
        SlackCredentialStore.SafePath(Active);
        SlackCredentialStore.SafePath(RecoveryPath);
        if (File.Exists(RecoveryPath))
        {
            SlackSetupRecovery recovery = JsonSerializer.Deserialize<SlackSetupRecovery>(File.ReadAllText(RecoveryPath)) ?? throw new JsonException();
            string saved = recovery.AppId ?? "";
            _view = new(SlackSetupStatus.NeedsAttention, null, null, SlackApi.Id(saved, 'A') ? saved : null,
                "A previous setup did not finish. Review your Slack apps before starting again, or connect the existing app with its tokens.");
        }
        if (Directory.Exists(Active))
        {
            string? appId = ReadAppId() ?? _view.AppId;
            // A process restart ends setup authorization locally. Retain only
            // the app reference, so recovery cannot silently create a second app.
            SaveRecoveryAsync(appId).GetAwaiter().GetResult();
            Directory.Delete(Active, true);
            _view = new(SlackSetupStatus.NeedsAttention, null, null, appId,
                "Setup was interrupted. Review your Slack apps and CLI authorization, then connect the existing app with its tokens.");
        }
    }

    public SlackSetupView View(string session)
    {
        lock (_gate) return _view with { Command = session == _session ? _view.Command : null };
    }

    public SlackSetupView Start(string session)
    {
        lock (_gate)
        {
            if (_cleanup.CurrentCount == 0 || !_operation.IsCompleted || _view.Status is SlackSetupStatus.AwaitingAuthorization or SlackSetupStatus.NeedsAttention || _connection.Installation is not null)
                throw new SlackFailure("Finish or cancel the current setup before starting another.");
            _session = session; _ticket = null; _team = null;
            _cancellation?.Dispose(); _cancellation = new(TimeSpan.FromMinutes(15));
            _view = new(SlackSetupStatus.Preparing, null, DateTimeOffset.UtcNow.AddMinutes(15), null, null);
            _operation = PrepareAsync(_cancellation.Token);
            _expiryStop?.Dispose(); _expiryStop = new();
            _expiry = ExpireAsync(_expiryStop.Token);
            return _view;
        }
    }

    private async Task ExpireAsync(CancellationToken token)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(15), token); }
        catch (OperationCanceledException) { return; }
        await CancelAsync();
        lock (_gate) if (_view.Status == SlackSetupStatus.Idle && _session is null)
            _view = _view with { Notice = "Slack setup expired. Any app already created remains in Slack; use its tokens to recover it." };
    }

    private async Task PrepareAsync(CancellationToken token)
    {
        try
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
                throw new SlackFailure("Automatic setup requires Linux x64 or ARM64. You can connect an existing Slack app below.");
            SlackCredentialStore.PrivateDirectory(Active); SlackCredentialStore.PrivateDirectory(Profile); SlackCredentialStore.PrivateDirectory(Project);
            SlackCredentialStore.PrivateDirectory(Path.Combine(Project, ".slack"));
            string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
            string expected = arch == "arm64" ? LinuxArm64Sha256 : LinuxX64Sha256;
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2), MaxResponseContentBufferSize = 100 * 1024 * 1024 };
            byte[] archive = await http.GetByteArrayAsync($"https://github.com/slackapi/slack-cli/releases/download/v{Version}/slack_cli_{Version}_linux_{arch}.tar.gz", token);
            if (!Convert.ToHexString(SHA256.HashData(archive)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new SlackFailure("The Slack setup helper failed checksum verification.");
            using var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            bool found = false;
            while (await reader.GetNextEntryAsync(cancellationToken: token) is { } entry)
                if (Path.GetFileName(entry.Name) == "slack" && entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.DataStream is not null)
                {
                    if (found || entry.Length > 150 * 1024 * 1024) throw new SlackFailure();
                    using var memory = new MemoryStream();
                    await entry.DataStream.CopyToAsync(memory, token);
                    await SlackCredentialStore.WriteAsync(Executable, memory.ToArray(), token);
                    File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); found = true;
                }
            if (!found) throw new SlackFailure();
            string hooks = JsonSerializer.Serialize(new { hooks = new System.Collections.Generic.Dictionary<string, string> { ["get-hooks"] = HookCommand("hooks") } });
            await SlackCredentialStore.WriteAsync(Path.Combine(Project, ".slack", "hooks.json"), Encoding.UTF8.GetBytes(hooks), token);
            await SlackCredentialStore.WriteAsync(Path.Combine(Project, "manifest.json"), Encoding.UTF8.GetBytes(Assets.Manifest), token);
            string output = await RunAsync(["auth", "login", "--no-prompt"], token);
            Match ticket = Regex.Match(output, @"/slackauthticket ([A-Za-z0-9._~+/=-]+)", RegexOptions.CultureInvariant);
            if (!ticket.Success) throw new SlackFailure("Slack did not return an authorization command. Try setup again.");
            lock (_gate)
            {
                _ticket = ticket.Groups[1].Value;
                _view = _view with { Status = SlackSetupStatus.AwaitingAuthorization, Command = "/slackauthticket " + _ticket };
            }
        }
        catch (Exception) { await FailedAsync("Slack setup could not start. Cancel setup and try again, or connect an existing app."); }
    }

    public SlackSetupView Confirm(string session, string challenge)
    {
        lock (_gate)
        {
            if (session != _session || _view.Status != SlackSetupStatus.AwaitingAuthorization || _ticket is null ||
                _cancellation is null || _cancellation.IsCancellationRequested || !Regex.IsMatch(challenge, "^[A-Za-z0-9-]{4,64}$", RegexOptions.CultureInvariant))
                throw new SlackFailure("The confirmation code or setup session has expired. Cancel setup and start again.");
            _view = _view with { Status = SlackSetupStatus.Installing, Command = null };
            _operation = InstallAsync(challenge, _cancellation.Token);
            return _view;
        }
    }

    private async Task InstallAsync(string challenge, CancellationToken token)
    {
        try
        {
            await RunAsync(["auth", "login", "--no-prompt", "--ticket", _ticket!, "--challenge", challenge], token);
            _ticket = null;
            string accounts = await RunAsync(["auth", "list"], token);
            Match team = Regex.Match(accounts, @"Team ID: (T[A-Z0-9]+)", RegexOptions.CultureInvariant);
            if (!team.Success) throw new SlackFailure();
            _team = team.Groups[1].Value;
            await DeployAsync(token);
        }
        catch (Exception) { await FailedAsync("Slack setup needs attention. If workspace approval is pending, approve the existing app in Slack and resume setup."); }
    }

    public SlackSetupView Resume(string session)
    {
        lock (_gate)
        {
            if (session != _session || !_operation.IsCompleted || _view.Status != SlackSetupStatus.NeedsAttention ||
                _view.AppId is null || _team is null || _cancellation is null || _cancellation.IsCancellationRequested)
                throw new SlackFailure("This setup cannot be resumed automatically. Connect the existing app using its tokens.");
            _view = _view with { Status = SlackSetupStatus.Installing, Notice = null };
            _operation = ResumeAsync(_cancellation.Token); return _view;
        }
    }

    private async Task ResumeAsync(CancellationToken token)
    {
        try { await DeployAsync(token); }
        catch (Exception) { await FailedAsync("Slack could not finish installing the existing app. Review its workspace approval, then resume."); }
    }

    private async Task DeployAsync(CancellationToken token)
    {
        // A crash or unknown creation outcome must remain visible after restart,
        // even if Slack did not yet return an app ID.
        await SaveRecoveryAsync(ReadAppId());
        await RunAsync(["deploy", "--team", _team!, "--app", ReadAppId() ?? "deployed", "--manifest-source", "local", "--hide-triggers", "--force"], token);
        string? appId = ReadAppId();
        lock (_gate) _view = _view with { AppId = appId };
        string path = Path.Combine(Project, "runtime.json");
        if (!File.Exists(path)) throw new SlackFailure();
        SlackRuntimeTokens runtime = JsonSerializer.Deserialize<SlackRuntimeTokens>(await File.ReadAllTextAsync(path, token)) ?? throw new SlackFailure();
        string appToken = runtime.AppToken, botToken = runtime.BotToken;
        SlackCredentials verified = await _api.VerifyAsync(appToken, botToken, token);
        if (verified.WorkspaceId != _team || appId is not null && verified.AppId != appId) throw new SlackFailure();
        bool icon = false;
        try
        {
            // Only this helper's private CLI profile is inspected. The optional
            // icon step gracefully falls back if Slack changes its profile shape.
            Dictionary<string, SlackProfileAccount>? profile = JsonSerializer.Deserialize<Dictionary<string, SlackProfileAccount>>(await File.ReadAllTextAsync(Path.Combine(Profile, "credentials.json"), token));
            if (profile?.TryGetValue(_team, out SlackProfileAccount? account) == true)
            { await _api.SetIconAsync(account.Token, verified.AppId, token); icon = true; }
        }
        catch (Exception error) when (error is not OperationCanceledException) { }
        await RunAsync(["auth", "logout", "--all"], token);
        // Reverify after revoking setup authorization: runtime independence is
        // part of connection success, not an assumption about Slack's token model.
        await _connection.ConnectAsync(appToken, botToken, token);
        Directory.Delete(Active, true);
        File.Delete(RecoveryPath);
        lock (_gate)
        {
            _expiryStop?.Cancel();
            _view = new(SlackSetupStatus.Complete, null, null, verified.AppId,
                icon ? null : "Connected. You can add the Goblin icon in Slack’s app settings.");
        }
    }

    private async Task FailedAsync(string notice)
    {
        lock (_gate) _view = _view with { Status = SlackSetupStatus.NeedsAttention, Command = null, AppId = ReadAppId() ?? _view.AppId, Notice = notice };
        if (_view.AppId is not null || File.Exists(RecoveryPath)) await SaveRecoveryAsync(_view.AppId);
    }

    public async Task CancelAsync()
    {
        await _cleanup.WaitAsync();
        try
        {
            Task operation;
            lock (_gate)
            {
                _expiryStop?.Cancel();
                _cancellation?.Cancel(); operation = _operation;
            }
            try { await operation; } catch (OperationCanceledException) { }
            string? appId = ReadAppId() ?? _view.AppId;
            if (_connection.Installation is null && (appId is not null || File.Exists(RecoveryPath))) await SaveRecoveryAsync(appId);
            bool revoked = true;
            if (File.Exists(Executable))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await RunAsync(["auth", "logout", "--all"], timeout.Token); } catch { revoked = false; }
            }
            if (Directory.Exists(Active)) Directory.Delete(Active, true);
            lock (_gate)
            {
                _ticket = null; _team = null; _session = null;
                _view = new(SlackSetupStatus.Idle, null, null, appId,
                    !revoked ? "Local setup was cleared. Review and revoke the temporary Slack CLI authorization in Slack." :
                    appId is not null ? "Setup cancelled. The app remains in Slack; reuse it through the existing-app connection form." : null);
            }
        }
        finally { _cleanup.Release(); }
    }

    private Task SaveRecoveryAsync(string? appId) => SlackCredentialStore.WriteAsync(RecoveryPath,
        JsonSerializer.SerializeToUtf8Bytes(new SlackSetupRecovery { AppId = appId }), CancellationToken.None);

    private string? ReadAppId()
    {
        string path = Path.Combine(Project, ".slack", "apps.json");
        if (!File.Exists(path)) return null;
        try
        {
            SlackCliApps? apps = JsonSerializer.Deserialize<SlackCliApps>(File.ReadAllText(path));
            return apps?.Apps?.Values.Select(app => app.AppId).FirstOrDefault(id => SlackApi.Id(id, 'A'));
        }
        catch (JsonException) { }
        return null;
    }

    private async Task<string> RunAsync(string[] args, CancellationToken token)
    {
        // The pinned CLI logs command arguments, including ticket/challenge
        // values. Discard its daily logs before any process starts. Cover a UTC
        // midnight boundary for the short-lived setup as well.
        string logs = Path.Combine(Profile, "logs");
        SlackCredentialStore.PrivateDirectory(logs);
        foreach (int offset in new[] { -1, 0, 1 })
        {
            string path = Path.Combine(logs, "slack-debug-" + DateTime.UtcNow.AddDays(offset).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + ".log");
            if (new FileInfo(path).LinkTarget == "/dev/null") continue;
            if (File.Exists(path)) File.Delete(path);
            File.CreateSymbolicLink(path, "/dev/null");
        }
        var start = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = Project,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };
        start.Environment.Clear();
        start.Environment[Env.Home] = Active;
        start.Environment[Env.TmpDir] = Active;
        start.Environment[Env.SlackDisableTelemetry] = "true";
        start.Environment[Env.Path] = "/usr/local/bin:/usr/bin:/bin";
        foreach (string argument in args.Concat(["--config-dir", Profile, "--no-color", "--skip-update"])) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new SlackFailure();
        process.StandardInput.Close();
        Task<string> output = ReadBoundedAsync(process.StandardOutput, token), errors = ReadBoundedAsync(process.StandardError, token);
        try
        {
            Task exited = process.WaitForExitAsync(token);
            Task reads = Task.WhenAll(output, errors);
            await Task.WhenAny(exited, reads);
            if (reads.IsFaulted) await reads;
            await exited;
            string text = await output + "\n" + await errors;
            if (process.ExitCode != 0) throw new SlackFailure();
            return text;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(output, errors); } catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); char[] buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
        { if (text.Length + count > 1024 * 1024) throw new SlackFailure(); text.Append(buffer, 0, count); }
        return text.ToString();
    }

    private static string HookCommand(string action) => Quote(Environment.ProcessPath!) + " " +
        (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet" ? Quote(Assembly.GetEntryAssembly()!.Location) + " " : "") +
        "--slack-hook " + action;

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public static async Task<int> HookAsync(string action)
    {
        switch (action)
        {
            case "hooks":
                Console.Write(JsonSerializer.Serialize(new SlackCliHooksResponse
                {
                    Hooks = new SlackCliHooks
                    { GetManifest = HookCommand("manifest"), Deploy = HookCommand("capture") },
                    Config = new SlackCliConfig { SdkManagedConnectionEnabled = true }
                }));
                return 0;
            case "manifest": Console.Write(Assets.Manifest); return 0;
            case "capture":
                string app = Environment.GetEnvironmentVariable(Env.SlackAppToken) ?? "", bot = Environment.GetEnvironmentVariable(Env.SlackBotToken) ?? "";
                if (!app.StartsWith("xapp-", StringComparison.Ordinal) || !bot.StartsWith("xoxb-", StringComparison.Ordinal)) return 1;
                await SlackCredentialStore.WriteAsync(Path.Combine(Environment.CurrentDirectory, "runtime.json"),
                    JsonSerializer.SerializeToUtf8Bytes(new { appToken = app, botToken = bot }), CancellationToken.None);
                return 0;
            default: return 1;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null) await CancelAsync();
        _expiryStop?.Cancel();
        await _expiry;
        _expiryStop?.Dispose();
        _cancellation?.Dispose();
    }
}
