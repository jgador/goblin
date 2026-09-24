using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application;
using Goblin.Application.Repositories;
using Goblin.Application.Work;
using Goblin.Application.Workspaces;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Execution;
using Goblin.Integrations.Codex;
using Goblin.Integrations.GitHub;
using Goblin.Persistence;
using Goblin.Web.Monitoring;
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
    public ISystemSource? SystemSource { get; init; }
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
        string? nodeName = builder.Configuration["GOBLIN_NODE_NAME"];
        if (options.SystemSource is not null) builder.Services.AddSingleton(options.SystemSource);
        else if (!string.IsNullOrWhiteSpace(nodeName))
            builder.Services.AddSingleton<ISystemSource>(_ =>
            {
                string tokenFile = builder.Configuration["GOBLIN_KUBERNETES_TOKEN_FILE"] ?? "/var/run/secrets/kubernetes.io/serviceaccount/token";
                string caFile = builder.Configuration["GOBLIN_KUBERNETES_CA_FILE"] ?? "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";
                return new KubernetesSystemSource(builder.Configuration["GOBLIN_KUBERNETES_URL"],
                    nodeName, builder.Configuration["GOBLIN_NAMESPACE"] ?? "goblin",
                    builder.Configuration["GOBLIN_EXECUTION_NAMESPACE"] ?? "agents", tokenFile, caFile);
            });
        builder.Services.AddSingleton(services => new SystemMonitor(services.GetService<ISystemSource>()));
        builder.Services.AddHostedService(services => services.GetRequiredService<SystemMonitor>());
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
            builder.Host.UseWolverine(messaging => ApplicationServices.ConfigureMessaging(messaging, databaseConnection, !string.IsNullOrWhiteSpace(executionNamespace)));
            builder.Services.AddWorkApplication();
            var workspaceLimits = new WorkspaceLimits(
                builder.Configuration.GetValue("GOBLIN_MAX_SANDBOXES", 2),
                builder.Configuration.GetValue<long>("GOBLIN_MAX_CHECKPOINT_BYTES", 134217728),
                builder.Configuration.GetValue<long>("GOBLIN_MAX_WORKSPACE_STORAGE_BYTES", 2147483648),
                builder.Configuration.GetValue("GOBLIN_MAX_CACHED_WORKSPACES", 4));
            if (workspaceLimits.MaxSandboxes < 1 || workspaceLimits.MaxArchiveBytes < 1 || workspaceLimits.MaxStorageBytes < workspaceLimits.MaxArchiveBytes || workspaceLimits.MaxCachedVolumes < 1)
                throw new InvalidOperationException("Invalid workspace capacity configuration.");
            builder.Services.AddSingleton(workspaceLimits);
            builder.Services.AddSingleton<WorkspaceArchive>();
            builder.Services.AddSingleton<IWorkspaceArchive>(services => services.GetRequiredService<WorkspaceArchive>());
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
                var sandboxOptions = new SandboxOptions(executionNamespace,
                    builder.Configuration["GOBLIN_EXECUTION_IMAGE"] ?? "goblin-auth:0.1.0", workspace.CodexHome,
                    builder.Configuration["GOBLIN_REPOSITORY_URL"] ?? "http://goblin-repository.goblin.svc:8788")
                {
                    CpuLimit = builder.Configuration["GOBLIN_SANDBOX_CPU_LIMIT"] ?? "2",
                    MemoryLimit = builder.Configuration["GOBLIN_SANDBOX_MEMORY_LIMIT"] ?? "2Gi"
                };
                builder.Services.AddSingleton(sandboxOptions);
                builder.Services.AddSingleton<IInspectionHost, InspectionHost>();
                builder.Services.AddSingleton<InspectionCoordinator>();
                builder.Services.AddHostedService(services => services.GetRequiredService<InspectionCoordinator>());
                builder.Services.AddSingleton<IExecutionHost>(services => options.ExecutionHost ?? new SandboxHost(kubernetes, sandboxOptions,
                    executionHost, services.GetRequiredService<IRepositoryBroker>(), services.GetRequiredService<IWorkspaceArchive>(), workspaceLimits));
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
        if (options.EnableWork)
        {
            builder.Services.AddSingleton<IModelCatalogSource, CodexModelCatalogSource>();
            builder.Services.AddSingleton<ModelCatalogStore>();
        }
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
            ("/settings/system.js", "settings/system.js", "text/javascript; charset=utf-8"),
            ("/settings/styles.css", "settings/styles.css", "text/css; charset=utf-8"),
            ("/styles.css", "work/styles.css", "text/css; charset=utf-8"),
            ("/work", "work/index.html", "text/html; charset=utf-8"),
            ("/work/app.js", "work/app.js", "text/javascript; charset=utf-8"),
            ("/work/presentation.js", "work/presentation.js", "text/javascript; charset=utf-8"),
            ("/work/surface.js", "work/surface.js", "text/javascript; charset=utf-8"),
            ("/work/workspace.js", "work/workspace.js", "text/javascript; charset=utf-8"),
            ("/work/styles.css", "work/styles.css", "text/css; charset=utf-8"),
            ("/assets/branding/icon.svg", "assets/branding/icon.svg", "image/svg+xml")
        }) staticFiles.Add(path, (await File.ReadAllBytesAsync(Path.Combine(options.AssetDirectory, file)), type));

        app.UseWebSockets();
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
                if (path.StartsWith("/internal/workspaces/", StringComparison.Ordinal))
                {
                    if (!repositoryListener || context.Connection.LocalPort != 8788 || request.Headers.ContainsKey("Origin") || !HttpMethods.IsGet(request.Method))
                        throw new PublicError("not_found", "This endpoint does not exist.", 404);
                    await next(context); return;
                }
                if (path.StartsWith("/internal/repository/", StringComparison.Ordinal))
                {
                    if (!repositoryListener || context.Connection.LocalPort != 8788 || request.Headers.ContainsKey("Origin"))
                        throw new PublicError("not_found", "This endpoint does not exist.", 404);
                    string[] segments = path.Split('/');
                    if (segments.Length < 5 || !long.TryParse(segments[3], out long attemptId)) throw new PublicError("not_found", "This endpoint does not exist.", 404);
                    await context.RequestServices.GetRequiredService<RepositoryBroker>().AuthorizeAsync(attemptId, request.Headers["X-Goblin-Repository"].ToString(), !path.EndsWith("/current", StringComparison.Ordinal), request.HttpContext.RequestAborted);
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
        app.MapGet("/api/system", (SystemMonitor monitor) => Results.Json(monitor.Current));
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
            app.MapPost("/internal/repository/{attemptId:long}/operation-id", async (long attemptId, RepositoryBroker broker, CancellationToken token) =>
                Results.Json(await broker.ReserveOperationIdAsync(attemptId, token)));
            app.MapPost("/internal/repository/{attemptId:long}/{operationId:long}/{kind}", async (long attemptId, long operationId, string kind, HttpContext context, RepositoryBroker broker) =>
                Results.Json(await broker.EnqueueAsync(attemptId, operationId, kind, context.Request.Body, context.RequestAborted)));
            app.MapGet("/internal/repository/{attemptId:long}/operations/{operationId:long}", async (long attemptId, long operationId, RepositoryBroker broker, CancellationToken token) =>
                Results.Json(await broker.StatusAsync(attemptId, operationId, token)));
            app.MapGet("/internal/repository/{attemptId:long}/current", async (long attemptId, RepositoryBroker broker, CancellationToken token) =>
                Results.Json(await broker.CurrentAsync(attemptId, token), ExecutionFiles.Json));
            app.MapGet("/internal/repository/{attemptId:long}/setup-memory", async (long attemptId, RepositorySetupStore store, CancellationToken token) =>
                Results.Json(await store.ReadAsync(attemptId, token), ExecutionFiles.Json));
            app.MapPost("/internal/repository/{attemptId:long}/setup-memory", async (long attemptId, HttpContext context, RepositorySetupStore store) =>
            {
                await store.SaveAsync(attemptId, context.Request.Body, context.RequestAborted);
                return Results.NoContent();
            });
            app.MapPost("/internal/repository/{attemptId:long}/checkpoint", async (long attemptId, HttpContext context, RepositoryBroker broker, WorkspaceArchive archive) =>
                Results.Json(await archive.SaveAsync(attemptId, int.Parse(context.Request.Headers["X-Goblin-Turn"].ToString(), System.Globalization.CultureInfo.InvariantCulture),
                    context.Request.Headers["X-Goblin-Commit"].ToString(), context.Request.Body, broker, context.RequestAborted), ExecutionFiles.Json));
            app.MapGet("/internal/repository/{attemptId:long}/restore", async (long attemptId, HttpContext context, RepositoryBroker broker, WorkspaceArchive archive, CancellationToken token) =>
            {
                WorkSnapshot work = await broker.CurrentAsync(attemptId, token);
                WorkspaceCheckpoint? saved = await archive.LatestAsync(work.Id, work.Attempts[^1].Target.Repository!.Repository, token);
                if (saved is null) return Results.NoContent();
                context.Response.Headers["X-Goblin-Checkpoint"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(saved, ExecutionFiles.Json)));
                return Results.File(await archive.ReadAsync(work.Id, saved.Id, token), "application/gzip");
            });
            app.MapGet("/internal/workspaces/{id:long}", async (long id, HttpContext context, InspectionStore sessions, WorkspaceArchive archive, CancellationToken token) =>
            {
                InspectionAllocation session = await sessions.AuthorizeRestoreAsync(id, context.Request.Headers["X-Goblin-Inspection"].ToString(), token);
                return Results.File(await archive.ReadAsync(session.WorkId, session.CheckpointId!.Value, token), "application/gzip");
            });
            app.MapGet("/api/work/{id:long}/workspace", async (long id, WorkspaceArchive archive, InspectionStore sessions, WorkStore store, CancellationToken token) =>
            {
                await store.GetAsync(id, token);
                return WorkResponse(new { checkpoints = await archive.ListAsync(id, token), sessions = await sessions.ListAsync(id, token), terminalAvailable = repositoryListener });
            });
            app.MapGet("/api/work/{id:long}/workspace/{checkpoint:long}/files", async (long id, long checkpoint, string? path, WorkspaceArchive archive, CancellationToken token) =>
                Results.Json(WorkspaceArchive.Inspect(await archive.ReadAsync(id, checkpoint, token), path)));
            app.MapGet("/api/work/{id:long}/workspace/{checkpoint:long}/download", async (long id, long checkpoint, WorkspaceArchive archive, CancellationToken token) =>
                Results.File(await archive.ReadAsync(id, checkpoint, token), "application/gzip", "work-" + id + "-workspace.tar.gz"));
            if (repositoryListener)
            {
                app.MapPost("/api/work/{id:long}/workspace/sessions", async (long id, HttpContext context, InspectionStore sessions) =>
                {
                    var body = (Dictionary<string, JsonElement>)context.Items[BodyKey]!;
                    InspectionRequest request = JsonSerializer.Deserialize<InspectionRequest>(JsonSerializer.Serialize(body), WorkStore.Json)!;
                    return WorkResponse(await sessions.OpenAsync(id, request.Id, request.AttemptId, request.CheckpointId, CancellationToken.None));
                });
                app.MapPost("/api/work/{id:long}/workspace/sessions/{session:long}/stop", async (long id, long session, InspectionStore sessions) =>
                { await sessions.StopAsync(id, session, CancellationToken.None); return Results.NoContent(); });
                app.MapGet("/api/work/{id:long}/workspace/sessions/{session:long}/terminal", async (long id, long session, HttpContext context, InspectionStore sessions, KubernetesApi api) =>
                    await WorkspaceTerminal.ConnectAsync(context, id, session, sessions, api, workspace));
            }
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
            app.MapGet("/api/connections/{id:long}/models", async (long id, int? limit, string? selected,
                ModelCatalogStore catalogs, CancellationToken token) =>
                WorkResponse(await catalogs.GetAsync(id, limit ?? 3, selected, token)));
            app.MapPost("/api/connections/{id:long}/models/refresh", async (long id,
                ModelCatalogStore catalogs, CancellationToken token) =>
            {
                catalogs.ScheduleRefresh(id, force: true);
                return WorkResponse(await catalogs.GetAsync(id, 3, null, token));
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
                if (store is not null)
                {
                    bool available = state.Account is not null && state.RuntimeReady;
                    bool changed = await store.SetConnectionAsync(WorkStore.DefaultAgentId,
                        available ? "Available" : "Disconnected", requireIdle: false,
                        completeChange: changing, observeAccount: true,
                        accountSignature: AccountSignature(state.Account));
                    if (available)
                    {
                        ModelCatalogStore catalogs = context.RequestServices.GetRequiredService<ModelCatalogStore>();
                        if (changed) catalogs.ScheduleRefresh(WorkStore.DefaultAgentId);
                        else
                            try { await catalogs.ObserveExecutableAsync(WorkStore.DefaultAgentId, context.RequestAborted); }
                            catch { /* Discovery cannot change the connection result. */ }
                    }
                }
                return state;
            }
            catch
            {
                if (store is not null) await store.SetConnectionAsync(WorkStore.DefaultAgentId, "Unavailable", requireIdle: false, completeChange: changing);
                throw;
            }
        }
    }

    private static string? AccountSignature(AccountView? account)
    {
        if (account is null) return null;
        string identity = account switch
        {
            ChatgptAccountView chatgpt => "chatgpt:" + chatgpt.Email?.Trim().ToLowerInvariant(),
            ApiKeyAccountView => "apiKey",
            _ => account.GetType().Name
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
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

public sealed record InspectionRequest(long Id, long AttemptId, long? CheckpointId);
