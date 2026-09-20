using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application;
using Goblin.Application.Repositories;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;
using Goblin.Execution;
using Goblin.Integrations.Codex;
using Goblin.Integrations.GitHub;
using Goblin.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Yarp.ReverseProxy.Forwarder;

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
    public string GitHubCommand { get; init; } = "gh";
    public string? HeadlampUrl { get; init; }
}

public static class GoblinApplication
{
    private const string BodyKey = "goblin.body";
    private const string SessionKey = "goblin.session";
    private static readonly JsonSerializerOptions WorkJson = new(WorkStore.Json)
    {
        Converters = { new LongJsonConverter() }
    };

    private static IResult WorkResponse<T>(T value) => Results.Json(value, WorkJson);

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
        if (!string.IsNullOrWhiteSpace(options.HeadlampUrl))
        {
            builder.Services.AddHttpForwarder();
            builder.Services.AddSingleton(services => new HeadlampProxy(services.GetRequiredService<IHttpForwarder>(), options.HeadlampUrl));
        }
        string? databaseConnection = builder.Configuration.GetConnectionString("Goblin");
        if (!string.IsNullOrWhiteSpace(databaseConnection)) builder.Services.AddGoblinPersistence(databaseConnection);
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        string? executionNamespace = builder.Configuration["GOBLIN_EXECUTION_NAMESPACE"];
        bool repositoryListener = options.EnableWork && !string.IsNullOrWhiteSpace(executionNamespace);
        builder.Services.AddSingleton(new GitHubConnection(Path.Combine(workspace.DataDirectory, "github-cli"), options.GitHubCommand));
        if (options.EnableWork)
        {
            if (string.IsNullOrWhiteSpace(databaseConnection)) throw new InvalidOperationException("Durable Work requires PostgreSQL.");
            builder.Host.UseWolverine(messaging => ApplicationServices.ConfigureMessaging(messaging, databaseConnection));
            builder.Services.AddWorkApplication();
            IExecutionHost executionHost = new LocalTextHost(new(
                Path.Combine(workspace.DataDirectory, "executions"), workspace.CodexHome, runtimeOptions.Command,
                typeof(GoblinApplication).Assembly.Location));
            builder.Services.AddSingleton<IRepositoryRemote, GitHubRepositoryRemote>();
            builder.Services.AddSingleton(new RepositoryBrokerOptions(Path.Combine(workspace.DataDirectory, "repositories")));
            builder.Services.AddSingleton<RepositoryBroker>();
            builder.Services.AddSingleton<IRepositoryBroker>(services => services.GetRequiredService<RepositoryBroker>());
            if (!string.IsNullOrWhiteSpace(executionNamespace))
            {
                var kubernetes = new KubernetesApi(builder.Configuration["GOBLIN_KUBERNETES_URL"],
                    builder.Configuration["GOBLIN_KUBERNETES_TOKEN_FILE"] ?? "/var/run/secrets/kubernetes.io/serviceaccount/token",
                    builder.Configuration["GOBLIN_KUBERNETES_CA_FILE"] ?? "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt");
                builder.Services.AddSingleton(kubernetes);
                builder.Services.AddSingleton<IExecutionHost>(services => options.ExecutionHost ?? new SandboxHost(kubernetes, new(executionNamespace,
                    builder.Configuration["GOBLIN_EXECUTION_IMAGE"] ?? "goblin-auth:0.1.0", workspace.CodexHome,
                    "http://goblin-repository.goblin.svc:8788"), executionHost, services.GetRequiredService<IRepositoryBroker>()));
            }
            else builder.Services.AddSingleton(options.ExecutionHost ?? executionHost);
            builder.Services.AddSingleton<IDispatchFailureJournal>(new FileDispatchFailureJournal(Path.Combine(workspace.DataDirectory, "dispatch-failures")));
        }
        builder.WebHost.UseUrls(repositoryListener ? [options.ListenUrl, "http://0.0.0.0:8788"] : [options.ListenUrl]);
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
        HeadlampProxy? headlamp = app.Services.GetService<HeadlampProxy>();
        var staticFiles = new Dictionary<string, (byte[] Body, string ContentType)>();
        foreach ((string? path, string? file, string? type) in new[]
        {
            ("/", "work/index.html", "text/html; charset=utf-8"),
            ("/app.js", "connection/app.js", "text/javascript; charset=utf-8"),
            ("/codex.js", "connection/codex.js", "text/javascript; charset=utf-8"),
            ("/connection/codex.js", "connection/codex.js", "text/javascript; charset=utf-8"),
            ("/connection/panel.html", "connection/panel.html", "text/html; charset=utf-8"),
            ("/settings/settings.js", "settings/settings.js", "text/javascript; charset=utf-8"),
            ("/settings/github.js", "settings/github.js", "text/javascript; charset=utf-8"),
            ("/settings/styles.css", "settings/styles.css", "text/css; charset=utf-8"),
            ("/styles.css", "work/styles.css", "text/css; charset=utf-8"),
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
                string path = request.Path.Value ?? "/";
                if (path.StartsWith("/internal/repository/", StringComparison.Ordinal))
                {
                    if (!repositoryListener || context.Connection.LocalPort != 8788 || request.Headers.ContainsKey("Origin"))
                        throw new PublicError("not_found", "This endpoint does not exist.", 404);
                    string[] segments = path.Split('/');
                    if (segments.Length < 5 || !long.TryParse(segments[3], out long attemptId)) throw new PublicError("not_found", "This endpoint does not exist.", 404);
                    await context.RequestServices.GetRequiredService<RepositoryBroker>().AuthorizeAsync(attemptId, request.Headers["X-Goblin-Repository"].ToString(), true, request.HttpContext.RequestAborted);
                    await next(context); return;
                }
                if (repositoryListener && context.Connection.LocalPort == 8788) throw new PublicError("not_found", "This endpoint does not exist.", 404);
                // Endpoint routing treats /work and /work/ as the same route.
                if (path == "/work/") path = "/work";
                bool get = HttpMethods.IsGet(request.Method);
                bool post = HttpMethods.IsPost(request.Method);
                if (get && path is "/healthz" or "/readyz") { await next(context); return; }
                workspace.ValidateRequest(request);
                if (request.Path.StartsWithSegments("/headlamp", StringComparison.Ordinal))
                {
                    if (headlamp is null) throw new PublicError("cluster_unavailable", "The cluster view requires Goblin's Kubernetes installation.", 404);
                    if (workspace.SessionId(request) is null)
                    {
                        if (get && request.Headers.Accept.ToString().Contains("text/html", StringComparison.Ordinal))
                        { response.Redirect("/?returnTo=headlamp"); return; }
                        throw new PublicError("workspace_locked", "Unlock the workspace to continue.", 401);
                    }
                    await headlamp.SendAsync(context, workspace); return;
                }
                bool publicRequest = (get && staticFiles.ContainsKey(path)) || (path == "/api/session" && (get || post));
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
                    IDbContextFactory<GoblinDbContext> factory = context.RequestServices.GetRequiredService<IDbContextFactory<GoblinDbContext>>();
                    await using GoblinDbContext db = await factory.CreateDbContextAsync(timeout.Token);
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
        app.MapGet("/api/cluster", () => Results.Json(new { available = headlamp is not null }));
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
                PromptResult result = await auth.SendPromptAsync(StringField(context, "prompt"), context.RequestAborted);
                available = true;
                return Results.Json(result);
            }
            finally { if (store is not null) await store.EndVerificationAsync(WorkStore.DefaultAgentId, available); }
        }));
        var githubGate = new SemaphoreSlim(1);
        async Task<GitHubState> GitHubAsync(HttpContext context, string action)
        {
            await githubGate.WaitAsync(context.RequestAborted);
            GitHubConnection github = context.RequestServices.GetRequiredService<GitHubConnection>();
            GitHubStore? store = options.EnableWork ? context.RequestServices.GetRequiredService<GitHubStore>() : null;
            try
            {
                if (action is "connect" or "disconnect" or "cancel")
                {
                    if (store is not null) await store.BeginChangeAsync(context.RequestAborted);
                }
                GitHubState state = action switch
                {
                    "connect" => await github.StartAsync(),
                    "disconnect" or "cancel" => await github.DisconnectAsync(),
                    "check" => await github.CheckAsync(context.RequestAborted),
                    _ => await github.StatusAsync()
                };
                if (store is not null) await store.ObserveAsync(state.Account, state.Status);
                return state;
            }
            finally { githubGate.Release(); }
        }
        app.MapGet("/api/github", (Delegate)((HttpContext context) => GitHubAsync(context, "status")));
        foreach (string action in new[] { "connect", "disconnect", "cancel", "check" })
            app.MapPost("/api/github/" + action, (Delegate)((HttpContext context) => GitHubAsync(context, action)));
        if (options.EnableWork)
        {
            app.MapGet("/api/github/repositories", async (GitHubStore store, CancellationToken token) => WorkResponse(await store.RepositoriesAsync(token)));
            app.MapGet("/api/github/available-repositories", async (GitHubConnection github, int? page, CancellationToken token) => WorkResponse(await github.RepositoriesAsync(page ?? 1, token)));
            app.MapPost("/api/github/repositories", async (HttpContext context, GitHubConnection github, GitHubStore store) =>
            {
                RepositoryAccount account = (await github.StatusAsync()).Account ?? throw new PublicError("repository_unavailable", "Connect GitHub first.", 409);
                RepositoryInfo repository = await github.RepositoryAsync(StringField(context, "repository") ?? "", context.RequestAborted);
                await store.SetRepositoryAsync(repository, StringField(context, "enabled") == "true", account.Generation, context.RequestAborted);
                return WorkResponse(await store.RepositoriesAsync());
            });
            app.MapGet("/internal/repository/{attemptId:long}/input", (long attemptId, RepositoryBroker broker) => Results.File(broker.InputPath(attemptId), "application/octet-stream"));
            app.MapPost("/internal/repository/{attemptId:long}/{operationId:guid}/{kind}", async (long attemptId, Guid operationId, string kind, HttpContext context, RepositoryBroker broker) =>
                Results.Json(await broker.EnqueueAsync(attemptId, operationId, kind, context.Request.Body, context.RequestAborted)));
            app.MapGet("/internal/repository/{attemptId:long}/operations/{operationId:guid}", async (long attemptId, Guid operationId, RepositoryBroker broker, CancellationToken token) =>
                Results.Json(await broker.StatusAsync(attemptId, operationId, token)));
            app.MapGet("/api/conversations", async (ConversationStore store, CancellationToken token) => WorkResponse(await store.ListAsync(token)));
            app.MapPost("/api/conversations/commands", async (HttpContext context, ConversationStore store) =>
            {
                var body = (Dictionary<string, JsonElement>)context.Items[BodyKey]!;
                ConversationCommand command = JsonSerializer.Deserialize<ConversationCommand>(JsonSerializer.Serialize(body), WorkStore.Json)
                    ?? throw new PublicError("invalid_command", "Send a conversation command.");
                return WorkResponse(await store.ApplyAsync(command));
            });
            app.MapGet("/api/work", async (WorkStore store, CancellationToken token) => WorkResponse(await store.ListAsync(token)));
            app.MapGet("/api/work/{id:long}", async (long id, WorkStore store, CancellationToken token) => WorkResponse(await store.GetAsync(id, token)));
            app.MapPost("/api/identities", async (HttpContext context, IdentityStore store, CancellationToken token) =>
            {
                var body = (Dictionary<string, JsonElement>)context.Items[BodyKey]!;
                IdentityRequest request = JsonSerializer.Deserialize<IdentityRequest>(JsonSerializer.Serialize(body), WorkStore.Json)
                    ?? throw new PublicError("invalid_command", "Specify the IDs to reserve.");
                return WorkResponse(await store.ReserveAsync(request, token));
            });
            app.MapGet("/api/agents", async (WorkStore store, CancellationToken token) => WorkResponse(await store.AgentsAsync(token)));
            app.MapGet("/api/connections", async (HttpContext context, WorkStore store, CancellationToken token) =>
            {
                try { await ConnectionAsync(context, false, auth.StatusAsync); } catch (IntegrationFailure) { }
                return WorkResponse(await store.ConnectionsAsync(token));
            });
            app.MapGet("/api/runtimes", (IExecutionHost host) => host.Capabilities);
            app.MapPost("/api/work/commands", async (HttpContext context, WorkStore store) =>
            {
                var body = (Dictionary<string, JsonElement>)context.Items[BodyKey]!;
                WorkCommand command = JsonSerializer.Deserialize<WorkCommand>(JsonSerializer.Serialize(body), WorkStore.Json)
                    ?? throw new PublicError("invalid_command", "Send a work command.");
                // Once accepted, the command has an independent transaction and
                // execution lifecycle. RequestAborted is deliberately not passed.
                return WorkResponse(await store.ApplyAsync(command));
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
        byte[] buffer = new byte[8193];
        int size = 0;
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

    private sealed class CodexRecovery : BackgroundService
    {
        private readonly CodexClient _codex;

        public CodexRecovery(CodexClient codex) => _codex = codex;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            bool firstAttempt = true;
            do
            {
                try { await _codex.StartAsync(stoppingToken); }
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
            await _codex.DisposeAsync();
            await base.StopAsync(cancellationToken);
        }
    }
}
