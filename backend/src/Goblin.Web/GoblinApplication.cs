using System;
using System.Threading.Tasks;
using Goblin.Integrations.Codex;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

namespace Goblin.Web;

public static class GoblinApplication
{
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
        WebRuntimeConfiguration configuration = WebServices.Configure(builder, options, workspace, runtimeOptions);
        bool gitRepositoryListener = options.EnableWork && !string.IsNullOrWhiteSpace(configuration.ExecutionNamespace);
        builder.WebHost.UseUrls(gitRepositoryListener ? [options.ListenUrl, "http://0.0.0.0:8788"] : [options.ListenUrl]);

        WebApplication app = builder.Build();
        // Resolve eagerly so event subscriptions exist before the first initialization.
        _ = app.Services.GetRequiredService<Authentication>();
        _ = app.Services.GetRequiredService<CodexClient>();
        if (options.EnableWork) _ = app.Services.GetRequiredService<Goblin.Integrations.Slack.SlackSetup>();
        StaticAssets assets = await StaticAssets.LoadAsync(options.AssetDirectory);

        app.UseWebSockets();
        if (options.EnableChatGpt)
        {
            var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto };
            forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("10.0.0.0/8"));
            forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.16.0.0/12"));
            forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("192.168.0.0/16"));
            app.UseForwardedHeaders(forwarded);
        }
        app.UseMiddleware<WorkspaceMiddleware>(assets, gitRepositoryListener);
        if (options.EnableChatGpt) app.UseAuthentication();
        new HealthEndpoints(options).Map(app);
        assets.Map(app);
        SessionEndpoints.Map(app);
        SettingsEndpoints.Map(app);
        new ConnectionEndpoints(options).Map(app);
        new GitHubEndpoints(options).Map(app);
        SlackEndpoints.Map(app, options.EnableWork);
        ChatGptMcp.Map(app);
        if (options.EnableWork)
        {
            GitRepositoryEndpoints.Map(app);
            new WorkspaceEndpoints(gitRepositoryListener).Map(app);
            ConversationEndpoints.Map(app);
            WorkEndpoints.Map(app);
        }
        app.MapFallback("/{**path}", () => Results.Json(new ApiFailure(new("not_found", "This endpoint does not exist.")), statusCode: 404));
        return app;
    }
}
