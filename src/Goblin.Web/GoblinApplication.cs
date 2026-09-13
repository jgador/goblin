using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Web.Codex;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Goblin.Web;

public sealed record ApplicationOptions
{
    public string DataDirectory { get; init; } = ".goblin-auth";
    public string? PasswordHashFile { get; init; }
    public string PublicOrigin { get; init; } = "http://localhost:8787";
    public string ListenUrl { get; init; } = "http://127.0.0.1:8787";
    public string AssetDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    public Func<CodexOptions, CodexOptions>? ConfigureCodex { get; init; }
    public Func<string, Task<string>>? VerifyApiKey { get; init; }
    public TimeSpan PromptTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public bool RecoverRuntime { get; init; } = true;
}

public static class GoblinApplication
{
    private const string BodyKey = "goblin.body";
    private const string SessionKey = "goblin.session";

    public static async Task<WebApplication> CreateAsync(ApplicationOptions options)
    {
        Workspace workspace = await Workspace.OpenAsync(options.DataDirectory, options.PublicOrigin, options.PasswordHashFile);
        var runtimeOptions = new CodexOptions { CodexHome = workspace.CodexHome, Home = workspace.Home, Workspace = workspace.WorkingDirectory };
        runtimeOptions = options.ConfigureCodex?.Invoke(runtimeOptions) ?? runtimeOptions;
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(GoblinApplication).Assembly.FullName });
        // Request and upstream details can contain credentials. Log only explicit safe startup messages.
        builder.Logging.ClearProviders();
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
            ("/", "index.html", "text/html; charset=utf-8"),
            ("/app.js", "app.js", "text/javascript; charset=utf-8"),
            ("/styles.css", "styles.css", "text/css; charset=utf-8"),
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
                PublicError safe = error as PublicError ?? new PublicError("internal_error", "The request could not be completed. Please retry.", 500);
                response.StatusCode = safe.Status;
                await response.WriteAsJsonAsync(new ApiFailure(new(safe.Code, safe.Message)));
            }
        });

        app.MapGet("/healthz", () => Results.Json(new { ok = true }));
        app.MapGet("/readyz", () => Results.Json(new { ready = codex.Ready }, statusCode: codex.Ready ? 200 : 503));
        foreach ((string? path, (byte[] Body, string ContentType) asset) in staticFiles)
            app.MapGet(path, () => Results.Bytes(asset.Body, asset.ContentType));
        app.MapGet("/api/session", (HttpContext context) => new SessionState(workspace.SessionId(context.Request) is not null, workspace.UsesPassword));
        app.MapPost("/api/session", (HttpContext context) => workspace.Unlock(StringField(context, "token"), context.Response));
        app.MapPost("/api/session/lock", (HttpContext context) => workspace.Lock((string)context.Items[SessionKey]!, context.Response));
        app.MapGet("/api/status", () => auth.StatusAsync());
        app.MapPost("/api/auth/chatgpt", () => auth.LoginChatGptAsync());
        app.MapPost("/api/auth/api-key", (Delegate)(async (HttpContext context) => Results.Json(await auth.LoginApiKeyAsync(StringField(context, "apiKey")))));
        app.MapPost("/api/auth/cancel", () => auth.CancelLoginAsync());
        app.MapPost("/api/auth/logout", () => auth.LogoutAsync());
        app.MapPost("/api/prompt", (Delegate)(async (HttpContext context) => Results.Json(await auth.TestPromptAsync(StringField(context, "prompt"), context.RequestAborted))));
        app.MapFallback("/{**path}", () => Results.Json(new ApiFailure(new("not_found", "This endpoint does not exist.")), statusCode: 404));
        return app;
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
                    if (firstAttempt) Console.Error.WriteLine("Codex could not start. Install the pinned Codex CLI, then restart the preview.");
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
