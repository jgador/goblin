using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Persistence;
using Goblin.Integrations.Codex;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;
using Goblin.Application;
using Goblin.Application.Work;
using Goblin.Execution;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Wolverine;
using Goblin.Integrations.GitHub;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Goblin.Web;

public sealed record ApplicationOptions
{
    public string DataDirectory { get; init; } = ".goblin-auth";
    public string? PasswordHashFile { get; init; }
    public string PublicOrigin { get; init; } = "http://localhost:8787";
    public bool AllowInsecureHttp { get; init; }
    public string ListenUrl { get; init; } = "http://127.0.0.1:8787";
    public string AssetDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    public Func<CodexOptions, CodexOptions>? ConfigureCodex { get; init; }
    public Func<string, Task<string>>? VerifyApiKey { get; init; }
    public TimeSpan PromptTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public bool RecoverRuntime { get; init; } = true;
    public bool EnableWork { get; init; }
    public IExecutionHost? ExecutionHost { get; init; }
}

public static class GoblinApplication
{
    private const string BodyKey = "goblin.body";
    private const string SessionKey = "goblin.session";

    public static async Task<WebApplication> CreateAsync(ApplicationOptions options)
    {
        Workspace workspace = await Workspace.OpenAsync(options.DataDirectory, options.PublicOrigin, options.PasswordHashFile,
            options.AllowInsecureHttp);
        var runtimeOptions = new CodexOptions { CodexHome = workspace.CodexHome, Home = workspace.Home, Workspace = workspace.WorkingDirectory };
        runtimeOptions = options.ConfigureCodex?.Invoke(runtimeOptions) ?? runtimeOptions;
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(GoblinApplication).Assembly.FullName,
            ContentRootPath = AppContext.BaseDirectory
        });
        // Request and upstream details can contain credentials. Log only explicit safe startup messages.
        builder.Logging.ClearProviders();
        string? databaseConnection = builder.Configuration.GetConnectionString("Goblin");
        if (!string.IsNullOrWhiteSpace(databaseConnection)) builder.Services.AddGoblinPersistence(databaseConnection);
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        if (options.EnableWork)
        {
            if (string.IsNullOrWhiteSpace(databaseConnection)) throw new InvalidOperationException("Durable Work requires PostgreSQL.");
            builder.Host.UseWolverine(messaging => ApplicationServices.ConfigureMessaging(messaging, databaseConnection));
            builder.Services.AddWorkApplication();
            IExecutionHost executionHost = new LocalTextHost(new(
                Path.Combine(workspace.DataDirectory, "executions"), workspace.CodexHome, runtimeOptions.Command,
                typeof(GoblinApplication).Assembly.Location));
            string? executionNamespace = builder.Configuration["GOBLIN_EXECUTION_NAMESPACE"];
            if (!string.IsNullOrWhiteSpace(executionNamespace))
            {
                var kubernetes = new KubernetesApi(builder.Configuration["GOBLIN_KUBERNETES_URL"],
                    builder.Configuration["GOBLIN_KUBERNETES_TOKEN_FILE"] ?? "/var/run/secrets/kubernetes.io/serviceaccount/token",
                    builder.Configuration["GOBLIN_KUBERNETES_CA_FILE"] ?? "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt");
                builder.Services.AddSingleton(kubernetes);
                executionHost = new SandboxHost(kubernetes, new(executionNamespace,
                    builder.Configuration["GOBLIN_EXECUTION_IMAGE"] ?? "goblin-auth:0.1.0", workspace.CodexHome,
                    Path.Combine(workspace.DataDirectory, "github.json")), executionHost);
            }
            builder.Services.AddSingleton(options.ExecutionHost ?? executionHost);
            builder.Services.AddSingleton(new GitHubConnection(builder.Configuration["GOBLIN_GITHUB_CLIENT_ID"], Path.Combine(workspace.DataDirectory, "github.json")));
            builder.Services.AddSingleton<IDispatchFailureJournal>(new FileDispatchFailureJournal(Path.Combine(workspace.DataDirectory, "dispatch-failures")));
        }
        builder.WebHost.UseUrls(options.ListenUrl);
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            server.Limits.MaxRequestHeaderCount = 32;
            server.Limits.MaxRequestBodySize = null; // ReadBodyAsync enforces the limit even for chunked input.
        });
        builder.Services.AddSingleton(workspace);
        builder.Services.AddSingleton(_ => new CodexClient(runtimeOptions));
        builder.Services.AddSingleton(_ => ApiKeyVerifier.CreateClient());
        builder.Services.AddSingleton<ApiKeyVerifier>();
        builder.Services.AddSingleton(services => new Authentication(services.GetRequiredService<CodexClient>(),
            options.VerifyApiKey ?? services.GetRequiredService<ApiKeyVerifier>().VerifyAsync, options.PromptTimeout));
        if (options.RecoverRuntime) builder.Services.AddHostedService<CodexRecovery>();
        WebApplication app = builder.Build();
        // Resolve eagerly so event subscriptions exist before the first initialization.
        Authentication auth = app.Services.GetRequiredService<Authentication>();
        CodexClient codex = app.Services.GetRequiredService<CodexClient>();
        var staticFiles = new Dictionary<string, (byte[] Body, string ContentType)>();
        foreach ((string? path, string? file, string? type) in new[]
        {
            ("/", "connection/index.html", "text/html; charset=utf-8"),
            ("/app.js", "connection/app.js", "text/javascript; charset=utf-8"),
            ("/styles.css", "connection/styles.css", "text/css; charset=utf-8"),
            ("/work", "work/index.html", "text/html; charset=utf-8"),
            ("/work/app.js", "work/app.js", "text/javascript; charset=utf-8"),
            ("/work/presentation.js", "work/presentation.js", "text/javascript; charset=utf-8"),
            ("/work/styles.css", "work/styles.css", "text/css; charset=utf-8"),
            ("/assets/branding/icon.svg", "assets/branding/icon.svg", "image/svg+xml")
        }) staticFiles.Add(path, (await File.ReadAllBytesAsync(Path.Combine(options.AssetDirectory, file)), type));

        app.Use(async (context, next) =>
        {
            HttpResponse response = context.Response;
            response.Headers.CacheControl = "no-store";
            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers.XFrameOptions = "DENY";
            response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            try
            {
                HttpRequest request = context.Request;
                var path = request.Path.Value ?? "/";
                // Endpoint routing treats /work and /work/ as the same route.
                if (path == "/work/") path = "/work";
                var get = HttpMethods.IsGet(request.Method);
                var post = HttpMethods.IsPost(request.Method);
                if (get && path is "/healthz" or "/readyz") { await next(context); return; }
                workspace.ValidateRequest(request);
                var publicRequest = (get && staticFiles.ContainsKey(path)) || (path == "/api/session" && (get || post));
                if (!publicRequest)
                    context.Items[SessionKey] = workspace.SessionId(request) ?? throw new PublicError("workspace_locked", "Unlock the workspace to continue.", 401);
                if (post) context.Items[BodyKey] = await ReadBodyAsync(request);
                await next(context);
            }
            catch (Exception error)
            {
                if (response.HasStarted || context.RequestAborted.IsCancellationRequested) return;
                PublicError safe = PublicError.Translate(error);
                response.StatusCode = safe.Status;
                await response.WriteAsJsonAsync(new ApiFailure(new(safe.Code, safe.Message)));
            }
        });

        app.MapGet("/healthz", () => Results.Json(new { ok = true }));
        app.MapGet("/readyz", async (HttpContext context) =>
        {
            bool ready = true;
            if (options.EnableWork)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var db = context.RequestServices.GetRequiredService<GoblinDbContext>();
                    await db.WorkItems.AsNoTracking().Select(x => x.Id).Take(1).ToArrayAsync(timeout.Token);
                }
                catch { ready = false; }
            }
            return Results.Json(new { ready }, statusCode: ready ? 200 : 503);
        });
        foreach ((string? path, (byte[] Body, string ContentType) asset) in staticFiles)
            app.MapGet(path, () => Results.Bytes(asset.Body, asset.ContentType));
        app.MapGet("/api/session", (HttpContext context) => workspace.Session(workspace.SessionId(context.Request) is not null));
        app.MapPost("/api/session", (HttpContext context) => workspace.Unlock(StringField(context, "password"), context.Response));
        app.MapPost("/api/session/lock", (HttpContext context) => workspace.Lock((string)context.Items[SessionKey]!, context.Response));
        app.MapGet("/api/status", (Delegate)((HttpContext context) => ConnectionAsync(context, false, auth.StatusAsync)));
        app.MapPost("/api/auth/chatgpt", (Delegate)((HttpContext context) => ConnectionAsync(context, true, auth.LoginChatGptAsync)));
        app.MapPost("/api/auth/api-key", (Delegate)((HttpContext context) => ConnectionAsync(context, true, () => auth.LoginApiKeyAsync(StringField(context, "apiKey")))));
        app.MapPost("/api/auth/cancel", (Delegate)((HttpContext context) => ConnectionAsync(context, true, auth.CancelLoginAsync)));
        app.MapPost("/api/auth/logout", (Delegate)((HttpContext context) => ConnectionAsync(context, true, auth.LogoutAsync)));
        app.MapPost("/api/prompt", (Delegate)(async (HttpContext context) =>
        {
            WorkStore? store = options.EnableWork ? context.RequestServices.GetRequiredService<WorkStore>() : null;
            if (store is not null) await store.BeginVerificationAsync(WorkStore.DefaultAgentId, context.RequestAborted);
            bool available = false;
            try
            {
                var result = await auth.SendPromptAsync(StringField(context, "prompt"), context.RequestAborted);
                available = true;
                return Results.Json(result);
            }
            finally { if (store is not null) await store.EndVerificationAsync(WorkStore.DefaultAgentId, available); }
        }));
        if (options.EnableWork)
        {
            app.MapGet("/api/github", (GitHubConnection github) => github.StatusAsync());
            app.MapPost("/api/github/connect", (GitHubConnection github) => github.StartAsync());
            app.MapPost("/api/github/disconnect", async (GitHubConnection github, WorkStore store) =>
            {
                await store.SetConnectionAsync(WorkStore.DefaultAgentId, "Changing", requireIdle: true);
                try { return await github.DisconnectAsync(); }
                finally { await store.SetConnectionAsync(WorkStore.DefaultAgentId, "Disconnected", requireIdle: false, completeChange: true); }
            });
            app.MapGet("/api/conversations", (ConversationStore store, CancellationToken token) => store.ListAsync(token));
            app.MapPost("/api/conversations/commands", async (HttpContext context, ConversationStore store) =>
            {
                var body = (Dictionary<string, JsonElement>)context.Items[BodyKey]!;
                var command = JsonSerializer.Deserialize<ConversationCommand>(JsonSerializer.Serialize(body), WorkStore.Json)
                    ?? throw new PublicError("invalid_command", "Send a conversation command.");
                return await store.ApplyAsync(command);
            });
            app.MapGet("/api/work", (WorkStore store, CancellationToken token) => store.ListAsync(token));
            app.MapGet("/api/work/{id:guid}", (Guid id, WorkStore store, CancellationToken token) => store.GetAsync(id, token));
            app.MapGet("/api/agents", (WorkStore store, CancellationToken token) => store.AgentsAsync(token));
            app.MapGet("/api/connections", async (HttpContext context, WorkStore store, CancellationToken token) =>
            {
                try { await ConnectionAsync(context, false, auth.StatusAsync); } catch (IntegrationFailure) { }
                return await store.ConnectionsAsync(token);
            });
            app.MapGet("/api/runtimes", (IExecutionHost host) => host.Capabilities);
            app.MapPost("/api/work/commands", async (HttpContext context, WorkStore store) =>
            {
                var body = (Dictionary<string, JsonElement>)context.Items[BodyKey]!;
                var command = JsonSerializer.Deserialize<WorkCommand>(JsonSerializer.Serialize(body), WorkStore.Json)
                    ?? throw new PublicError("invalid_command", "Send a work command.");
                // Once accepted, the command has an independent transaction and
                // execution lifecycle. RequestAborted is deliberately not passed.
                return await store.ApplyAsync(command);
            });
        }
        app.MapFallback("/{**path}", () => Results.Json(new ApiFailure(new("not_found", "This endpoint does not exist.")), statusCode: 404));
        return app;

        async Task<AuthenticationState> ConnectionAsync(HttpContext context, bool changing, Func<Task<AuthenticationState>> action)
        {
            WorkStore? store = options.EnableWork ? context.RequestServices.GetRequiredService<WorkStore>() : null;
            if (changing && store is not null) await store.SetConnectionAsync(WorkStore.DefaultAgentId, "Changing", requireIdle: true);
            try
            {
                AuthenticationState state = await action();
                if (store is not null) await store.SetConnectionAsync(WorkStore.DefaultAgentId,
                    state.Account is not null && state.RuntimeReady ? "Available" : "Disconnected", requireIdle: false, completeChange: changing);
                return state;
            }
            catch
            {
                if (store is not null) await store.SetConnectionAsync(WorkStore.DefaultAgentId, "Unavailable", requireIdle: false, completeChange: changing);
                throw;
            }
        }
    }

    private static string? StringField(HttpContext context, string name)
    {
        var body = (Dictionary<string, JsonElement>)context.Items[BodyKey]!;
        return body.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static async Task<Dictionary<string, JsonElement>> ReadBodyAsync(HttpRequest request)
    {
        if (request.ContentType?.Split(';')[0].Trim() != "application/json")
            throw new PublicError("invalid_content_type", "Send JSON content.", 415);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.HttpContext.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var buffer = new byte[8193];
        var size = 0;
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(buffer.AsMemory(size), timeout.Token)) > 0)
            {
                size += read;
                if (size > 8192) throw new PublicError("request_too_large", "The request is too large.", 413);
            }
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(buffer.AsSpan(0, size)) ?? throw new JsonException();
        }
        catch (JsonException) { throw new PublicError("invalid_json", "The request could not be read."); }
        catch (OperationCanceledException) { throw new PublicError("invalid_request", "The request was interrupted."); }
        catch (IOException) { throw new PublicError("invalid_request", "The request was interrupted."); }
    }

    private sealed class CodexRecovery(CodexClient codex) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            var firstAttempt = true;
            do
            {
                try { await codex.StartAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch
                {
                    if (firstAttempt) Console.Error.WriteLine("Codex could not start. Install the pinned Codex CLI, then restart Goblin.");
                }
                firstAttempt = false;
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await codex.DisposeAsync();
            await base.StopAsync(cancellationToken);
        }
    }
}
