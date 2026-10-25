using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Goblin.Database;
using Goblin.Integrations.ChatGpt;
using Goblin.Persistence;
using Goblin.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Npgsql;
using Xunit;
using Env = Goblin.Contracts.Configuration.EnvironmentVariables;

namespace Goblin.Tests;

public sealed class OAuthDatabaseFactAttribute : FactAttribute
{
    public OAuthDatabaseFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Env.GoblinTestPostgresAdmin) is null || Environment.GetEnvironmentVariable(Env.GoblinTestPostgresApp) is null)
            Skip = "Set PostgreSQL test connections to exercise the embedded OAuth server.";
    }
}

public sealed class ChatGptOAuthTests
{
    private const string Origin = "https://goblin.example.test";
    private const string Callback = "https://chatgpt.com/connector_platform_oauth_redirect";
    private const string Password = "fixture-owner-password";

    [Fact]
    public void OwnerIdentityAndKeysSurviveRestartAndFailClosedWhenCorrupt()
    {
        string directory = DirectoryPath();
        try
        {
            using var first = new ChatGptOAuthKeys(new(Origin), directory);
            using var second = new ChatGptOAuthKeys(new(Origin), directory);
            Assert.Equal(first.OwnerSubject, second.OwnerSubject);
            Assert.Equal(first.Signing.Thumbprint, second.Signing.Thumbprint);
            Assert.Equal(first.Encryption.Thumbprint, second.Encryption.Thumbprint);
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(directory, "chatgpt-oauth", "signing.pfx")));
            string encryption = Path.Combine(directory, "chatgpt-oauth", "encryption.pfx");
            byte[] saved = File.ReadAllBytes(encryption);
            File.WriteAllBytes(encryption, [0, 1, 2]);
            Assert.Throws<CryptographicException>(() => new ChatGptOAuthKeys(new(Origin), directory));
            File.WriteAllBytes(encryption, saved);
            File.WriteAllText(Path.Combine(directory, "chatgpt-oauth", "owner-subject"), "invalid");
            Assert.Throws<InvalidOperationException>(() => new ChatGptOAuthKeys(new(Origin), directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [OAuthDatabaseFact]
    public async Task OwnerConsentPkceTokensAndWorkGrantsSurviveRestartAndCanBeRevoked()
    {
        await using Database database = await Database.CreateAsync();
        string directory = DirectoryPath();
        try
        {
            string secret;
            string subject;
            string access;
            string refresh;
            await using (Host host = await Host.CreateAsync(database.App, directory))
            {
                using HttpClient owner = host.Client();
                await LoginAsync(owner);
                using HttpResponseMessage registration = await owner.PostAsync("/api/integrations/chatgpt/client", Json(new { redirectUri = "", rotateSecret = false }));
                Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
                secret = (await DocumentAsync(registration)).GetProperty("clientSecret").GetString()!;
                using HttpResponseMessage saved = await owner.PostAsync("/api/integrations/chatgpt/client", Json(new { redirectUri = Callback }));
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
                Assert.Equal(JsonValueKind.Null, (await DocumentAsync(saved)).GetProperty("clientSecret").ValueKind);
                using HttpResponseMessage setup = await owner.GetAsync("/api/integrations/chatgpt");
                JsonElement settings = await DocumentAsync(setup);
                subject = settings.GetProperty("ownerSubject").GetString()!;
                Assert.DoesNotContain(secret, settings.GetRawText());
                using HttpResponseMessage discovery = await owner.GetAsync("/.well-known/openid-configuration");
                Assert.Equal("no-referrer", discovery.Headers.GetValues("Referrer-Policy").Single());
                JsonElement metadata = await DocumentAsync(discovery);
                Assert.Equal(Origin + "/", metadata.GetProperty("issuer").GetString());
                Assert.Contains("S256", metadata.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(x => x.GetString()));
                Assert.DoesNotContain("plain", metadata.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(x => x.GetString()));
                using HttpResponseMessage keys = await owner.GetAsync("/oauth/keys");
                Assert.DoesNotContain("\"d\":", (await DocumentAsync(keys)).GetRawText());
                using HttpClient browser = host.Client();
                string verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
                Dictionary<string, string?> parameters = Parameters(verifier);
                string authorize = QueryHelpers.AddQueryString("/oauth/authorize", parameters);
                using HttpResponseMessage login = await browser.GetAsync(authorize);
                if (login.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException("Authorization failed: " + await login.Content.ReadAsStringAsync());
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                Assert.Equal("same-origin", login.Headers.GetValues("Referrer-Policy").Single());
                string loginPage = await login.Content.ReadAsStringAsync();
                Assert.Contains("Unlock Goblin", loginPage);
                string returnTo = WebUtility.HtmlDecode(Regex.Match(loginPage, "name=\"returnTo\" value=\"([^\"]+)\"").Groups[1].Value);
                using HttpResponseMessage unlocked = await browser.PostAsync("/oauth/unlock", new FormUrlEncodedContent(new Dictionary<string, string> { ["returnTo"] = returnTo, ["password"] = Password }));
                Assert.Equal(HttpStatusCode.Redirect, unlocked.StatusCode);
                browser.DefaultRequestHeaders.Add("Cookie", unlocked.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
                using HttpResponseMessage consent = await browser.GetAsync(authorize);
                Assert.Equal("same-origin", consent.Headers.GetValues("Referrer-Policy").Single());
                Assert.Contains("form-action 'self' " + Callback, consent.Headers.GetValues("Content-Security-Policy").Single());
                Assert.Contains("Allow connection", await consent.Content.ReadAsStringAsync());
                parameters["decision"] = "allow";
                using HttpResponseMessage allowed = await browser.PostAsync("/oauth/authorize", new FormUrlEncodedContent(parameters!));
                Assert.Equal(HttpStatusCode.Redirect, allowed.StatusCode);
                Uri callback = allowed.Headers.Location!;
                Assert.Equal("chatgpt.com", callback.Host);
                Dictionary<string, StringValues> query = QueryHelpers.ParseQuery(callback.Query);
                Assert.Equal("fixture-state", query["state"].ToString());
                Assert.Equal(Origin + "/", query["iss"].ToString());
                string code = query["code"].ToString();
                using HttpClient exchange = host.Client();
                exchange.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(ChatGptConfiguration.ClientId + ":" + secret)));
                using HttpResponseMessage tokens = await exchange.PostAsync("/oauth/token", Code(code, verifier));
                if (tokens.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException("Code exchange failed: " + await tokens.Content.ReadAsStringAsync());
                Assert.Equal(HttpStatusCode.OK, tokens.StatusCode);
                JsonElement issued = await DocumentAsync(tokens);
                access = issued.GetProperty("access_token").GetString()!;
                refresh = issued.GetProperty("refresh_token").GetString()!;
                Assert.DoesNotContain(Password, issued.GetRawText());
                using HttpResponseMessage replay = await exchange.PostAsync("/oauth/token", Code(code, verifier));
                Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
                // A code replay can revoke its authorization; obtain a fresh grant for
                // the persistence journey instead of depending on replay side effects.
                access = "";
                refresh = "";
                (access, refresh) = await AuthorizeAsync(host, owner, secret);
                using HttpClient mcp = host.Client();
                mcp.DefaultRequestHeaders.Authorization = new("Bearer", access);
                await using var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(Origin + "/mcp"), OAuth = null, TransportMode = HttpTransportMode.StreamableHttp }, mcp, ownsHttpClient: false);
                await using McpClient client = await McpClient.CreateAsync(transport);
                Assert.Equal(4, (await client.ListToolsAsync()).Count);
                CallToolResult empty = await client.CallToolAsync("goblin_list_work");
                Assert.Contains("\"items\":[]", JsonSerializer.Serialize(empty));
                await database.SeedWorkAsync();
                using HttpResponseMessage share = await owner.PostAsync("/api/integrations/chatgpt/grants", Json(new { workId = "101", hours = 24, subject = "untrusted-subject" }));
                Assert.Equal(HttpStatusCode.OK, share.StatusCode);
                CallToolResult shared = await client.CallToolAsync("goblin_list_work");
                Assert.Contains("Shared objective", JsonSerializer.Serialize(shared));
                Assert.DoesNotContain("Private objective", JsonSerializer.Serialize(shared));
                Assert.True((await client.CallToolAsync("goblin_get_work_status", new Dictionary<string, object?> { ["workId"] = "102" })).IsError);
            }
            await using (Host restarted = await Host.CreateAsync(database.App, directory))
            {
                using HttpClient owner = restarted.Client();
                await LoginAsync(owner);
                using HttpResponseMessage setup = await owner.GetAsync("/api/integrations/chatgpt");
                JsonElement state = await DocumentAsync(setup);
                Assert.Equal(subject, state.GetProperty("ownerSubject").GetString());
                Assert.Equal(subject, state.GetProperty("grants")[0].GetProperty("subject").GetString());
                using HttpClient exchange = restarted.Client();
                exchange.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(ChatGptConfiguration.ClientId + ":" + secret)));
                using HttpResponseMessage rotated = await exchange.PostAsync("/oauth/token", Refresh(refresh));
                Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
                JsonElement renewed = await DocumentAsync(rotated);
                string renewedAccess = renewed.GetProperty("access_token").GetString()!;
                string renewedRefresh = renewed.GetProperty("refresh_token").GetString()!;
                Assert.NotEqual(refresh, renewedRefresh);
                using HttpResponseMessage reused = await exchange.PostAsync("/oauth/token", Refresh(refresh));
                Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
                using HttpClient bearer = restarted.Client();
                bearer.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
                (renewedAccess, renewedRefresh) = await AuthorizeAsync(restarted, owner, secret);
                bearer.DefaultRequestHeaders.Authorization = new("Bearer", renewedAccess);
                using HttpResponseMessage valid = await bearer.PostAsync("/mcp", Json(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "fixture", version = "1" } } }));
                Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
                using HttpResponseMessage revoked = await owner.PostAsync("/api/integrations/chatgpt/client/revoke", Json(new { }));
                Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
                using HttpResponseMessage denied = await bearer.PostAsync("/mcp", Json(new { }));
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                using HttpResponseMessage refreshDenied = await exchange.PostAsync("/oauth/token", Refresh(renewedRefresh));
                Assert.Equal(HttpStatusCode.BadRequest, refreshDenied.StatusCode);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [OAuthDatabaseFact]
    public async Task OAuthRejectsWrongCallbacksResourcesProofsScopesAndCrossOriginConsent()
    {
        await using Database database = await Database.CreateAsync();
        string directory = DirectoryPath();
        try
        {
            await using Host host = await Host.CreateAsync(database.App, directory);
            using HttpClient owner = host.Client();
            await LoginAsync(owner);
            using HttpResponseMessage saved = await owner.PostAsync("/api/integrations/chatgpt/client", Json(new { redirectUri = Callback }));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            string secret = (await DocumentAsync(saved)).GetProperty("clientSecret").GetString()!;
            using HttpResponseMessage wildcard = await owner.PostAsync("/api/integrations/chatgpt/client", Json(new { redirectUri = "https://chatgpt.com/*" }));
            Assert.Equal(HttpStatusCode.BadRequest, wildcard.StatusCode);
            string verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
            foreach (string invalid in new[] { "resource", "scope", "pkce", "callback" })
            {
                Dictionary<string, string?> parameters = Parameters(verifier);
                if (invalid == "resource") parameters["resource"] = "https://other.example.test/mcp";
                if (invalid == "scope") parameters["scope"] = "openid";
                if (invalid == "pkce") parameters["code_challenge_method"] = "plain";
                if (invalid == "callback") parameters["redirect_uri"] = "https://other.example.test/callback";
                using HttpResponseMessage rejected = await owner.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize", parameters));
                Assert.Contains(rejected.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Redirect });
                if (rejected.Headers.Location is { } location)
                {
                    Assert.Equal("chatgpt.com", location.Host);
                    Dictionary<string, StringValues> query = QueryHelpers.ParseQuery(location.Query);
                    Assert.False(query.ContainsKey("code"));
                    Assert.True(query.ContainsKey("error"));
                }
            }
            Dictionary<string, string?> valid = Parameters(verifier);
            valid["decision"] = "allow";
            owner.DefaultRequestHeaders.Remove("Origin");
            foreach (string path in new[] { "/oauth/authorize", "/OAUTH/AUTHORIZE/", "/oauth/unlock", "/OAUTH/UNLOCK/" })
            {
                using HttpResponseMessage csrf = await owner.PostAsync(path, new FormUrlEncodedContent(valid!));
                Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
            }
            owner.DefaultRequestHeaders.Add("Origin", Origin);
            using HttpResponseMessage insecure = await owner.GetAsync("http://goblin.example.test/.well-known/openid-configuration");
            Assert.Equal(HttpStatusCode.Forbidden, insecure.StatusCode);
            using HttpClient anonymous = host.Client();
            using HttpResponseMessage noOwner = await anonymous.PostAsync("/oauth/authorize", new FormUrlEncodedContent(valid!));
            Assert.Equal(HttpStatusCode.OK, noOwner.StatusCode);
            Assert.Contains("Unlock Goblin", await noOwner.Content.ReadAsStringAsync());
            using HttpResponseMessage grant = await owner.PostAsync("/oauth/authorize", new FormUrlEncodedContent(valid!));
            Assert.Equal(HttpStatusCode.Redirect, grant.StatusCode);
            string code = QueryHelpers.ParseQuery(grant.Headers.Location!.Query)["code"].ToString();
            using HttpClient exchange = host.Client();
            exchange.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(ChatGptConfiguration.ClientId + ":wrong-secret")));
            using HttpResponseMessage wrongSecret = await exchange.PostAsync("/oauth/token", Code(code, verifier));
            Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
            exchange.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(ChatGptConfiguration.ClientId + ":" + secret)));
            using HttpResponseMessage wrongProof = await exchange.PostAsync("/oauth/token", Code(code, Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32))));
            Assert.Equal(HttpStatusCode.BadRequest, wrongProof.StatusCode);
            using HttpResponseMessage wrongTarget = await exchange.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = Callback, ["resource"] = "https://other.example.test/mcp" }));
            Assert.Equal(HttpStatusCode.BadRequest, wrongTarget.StatusCode);
            using HttpResponseMessage oversized = await exchange.PostAsync("/oauth/token", new StringContent("padding=" + new string('a', 8192), Encoding.UTF8, "application/x-www-form-urlencoded"));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
            (string access, string refresh) = await AuthorizeAsync(host, owner, secret);
            using HttpResponseMessage escalation = await exchange.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["scope"] = "admin", ["resource"] = Origin + "/mcp" }));
            Assert.Equal(HttpStatusCode.BadRequest, escalation.StatusCode);
            using HttpClient bearer = host.Client();
            bearer.DefaultRequestHeaders.Authorization = new("Bearer", access);
            using HttpResponseMessage administration = await bearer.PostAsync("/api/integrations/chatgpt/client", Json(new { redirectUri = Callback }));
            Assert.Equal(HttpStatusCode.Unauthorized, administration.StatusCode);
            using HttpResponseMessage rotation = await owner.PostAsync("/api/integrations/chatgpt/client", Json(new { redirectUri = Callback, rotateSecret = true }));
            Assert.Equal(HttpStatusCode.OK, rotation.StatusCode);
            string nextSecret = (await DocumentAsync(rotation)).GetProperty("clientSecret").GetString()!;
            Assert.NotEqual(secret, nextSecret);
            using HttpResponseMessage revokedAccess = await bearer.PostAsync("/mcp", Json(new { }));
            Assert.Equal(HttpStatusCode.Unauthorized, revokedAccess.StatusCode);
            using HttpResponseMessage oldSecret = await exchange.PostAsync("/oauth/token", Refresh(refresh));
            Assert.Equal(HttpStatusCode.Unauthorized, oldSecret.StatusCode);
            exchange.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(ChatGptConfiguration.ClientId + ":" + nextSecret)));
            using HttpResponseMessage revokedRefresh = await exchange.PostAsync("/oauth/token", Refresh(refresh));
            Assert.Equal(HttpStatusCode.BadRequest, revokedRefresh.StatusCode);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static Dictionary<string, string?> Parameters(string verifier) => new()
    {
        ["client_id"] = ChatGptConfiguration.ClientId,
        ["response_type"] = "code",
        ["redirect_uri"] = Callback,
        ["scope"] = "openid offline_access " + ChatGptConfiguration.ReadScope,
        ["resource"] = Origin + "/mcp",
        ["state"] = "fixture-state",
        ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
        ["code_challenge_method"] = "S256"
    };

    private static FormUrlEncodedContent Code(string code, string verifier) => new(new Dictionary<string, string>
    { ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = Callback, ["resource"] = Origin + "/mcp" });

    private static FormUrlEncodedContent Refresh(string refresh) => new(new Dictionary<string, string>
    { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["resource"] = Origin + "/mcp" });

    private static async Task<(string Access, string Refresh)> AuthorizeAsync(Host host, HttpClient owner, string secret)
    {
        string verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        Dictionary<string, string?> parameters = Parameters(verifier);
        parameters["decision"] = "allow";
        using HttpResponseMessage consent = await owner.PostAsync("/oauth/authorize", new FormUrlEncodedContent(parameters!));
        Assert.Equal(HttpStatusCode.Redirect, consent.StatusCode);
        string code = QueryHelpers.ParseQuery(consent.Headers.Location!.Query)["code"].ToString();
        using HttpClient exchange = host.Client();
        exchange.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(ChatGptConfiguration.ClientId + ":" + secret)));
        using HttpResponseMessage token = await exchange.PostAsync("/oauth/token", Code(code, verifier));
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        JsonElement issued = await DocumentAsync(token);
        return (issued.GetProperty("access_token").GetString()!, issued.GetProperty("refresh_token").GetString()!);
    }

    private static async Task LoginAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsync("/api/session", Json(new { password = Password }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
    }

    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> DocumentAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static string Repository()
    {
        string? root = Environment.CurrentDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "AGENTS.md"))) root = Path.GetDirectoryName(root);
        return root ?? throw new InvalidOperationException("Repository not found.");
    }

    private static string DirectoryPath() => Path.Combine(Repository(), ".artifacts", "openiddict", "oauth-tests", Guid.NewGuid().ToString("N"));

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private Host(WebApplication app) => _app = app;

        public static async Task<Host> CreateAsync(string connection, string directory)
        {
            Directory.CreateDirectory(directory);
            string passwordFile = Path.Combine(directory, "owner-password");
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            await File.WriteAllTextAsync(passwordFile, "pbkdf2-sha256$600000$" + Convert.ToBase64String(salt) + "$" +
                Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(Password, salt, 600000, HashAlgorithmName.SHA256, 32)));
            Workspace workspace = await Workspace.OpenAsync(directory, Origin, passwordFile);
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(workspace);
            builder.Services.AddGoblinPersistence(connection);
            ChatGptMcp.Configure(builder.Services, new ApplicationOptions { EnableWork = true, EnableChatGpt = true, PublicOrigin = Origin, DataDirectory = directory });
            WebApplication app = builder.Build();
            StaticAssets assets = await StaticAssets.LoadAsync(Path.Combine(Repository(), "frontend", "dist"));
            app.UseMiddleware<WorkspaceMiddleware>(assets, false);
            app.UseAuthentication();
            SessionEndpoints.Map(app);
            ChatGptMcp.Map(app);
            await app.StartAsync();
            return new Host(app);
        }

        public HttpClient Client()
        {
            HttpClient client = _app.GetTestClient();
            client.BaseAddress = new Uri(Origin);
            client.DefaultRequestHeaders.Add("Origin", Origin);
            return client;
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }

    private sealed class Database : IAsyncDisposable
    {
        private readonly string _name;
        private readonly string _admin;
        public string App { get; }

        private Database(string name, string admin, string app) { _name = name; _admin = admin; App = app; }

        public static async Task<Database> CreateAsync()
        {
            var admin = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Env.GoblinTestPostgresAdmin));
            var app = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Env.GoblinTestPostgresApp));
            string name = "goblin_oauth_test_" + Guid.NewGuid().ToString("N");
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await new NpgsqlCommand("CREATE DATABASE " + name, connection).ExecuteNonQueryAsync();
            string administrative = admin.ConnectionString;
            admin.Database = app.Database = name;
            await using var db = new NpgsqlConnection(admin.ConnectionString);
            await db.OpenAsync();
            await new NpgsqlCommand("ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO goblin_app; ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO goblin_app;", db).ExecuteNonQueryAsync();
            await SqlMigrations.ApplyAsync(admin.ConnectionString, Path.Combine(Repository(), "backend", "database", "migrations"), TextWriter.Null);
            return new Database(name, administrative, app.ConnectionString);
        }

        public async Task SeedWorkAsync()
        {
            await using var db = new NpgsqlConnection(App);
            await db.OpenAsync();
            await new NpgsqlCommand("INSERT INTO work_item (id, objective, state) VALUES (101, 'Shared objective', '{}'), (102, 'Private objective', '{}');", db).ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var db = new NpgsqlConnection(_admin);
            await db.OpenAsync();
            await new NpgsqlCommand("DROP DATABASE " + _name + " WITH (FORCE)", db).ExecuteNonQueryAsync();
        }
    }
}
