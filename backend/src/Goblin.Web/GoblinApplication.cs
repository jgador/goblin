using System;
using System.Threading.Tasks;
using Goblin.Integrations.Codex;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
        WebServices.Configure(builder, options, workspace, runtimeOptions);
        bool repositoryListener = options.EnableWork && !string.IsNullOrWhiteSpace(builder.Configuration["GOBLIN_EXECUTION_NAMESPACE"]);
        builder.WebHost.UseUrls(repositoryListener ? [options.ListenUrl, "http://0.0.0.0:8788"] : [options.ListenUrl]);

        WebApplication app = builder.Build();
        // Resolve eagerly so event subscriptions exist before the first initialization.
        Authentication auth = app.Services.GetRequiredService<Authentication>();
        _ = app.Services.GetRequiredService<CodexClient>();
        StaticAssets assets = await StaticAssets.LoadAsync(options.AssetDirectory);

        app.UseWebSockets();
        app.UseMiddleware<WorkspaceMiddleware>(assets, repositoryListener);
        new HealthEndpoints(options).Map(app);
        assets.Map(app);
        SessionEndpoints.Map(app);
        SettingsEndpoints.Map(app);
        new ConnectionEndpoints(options, auth).Map(app);
        new GitHubEndpoints(options).Map(app);
        if (options.EnableWork)
        {
            RepositoryEndpoints.Map(app);
            new WorkspaceEndpoints(repositoryListener).Map(app);
            ConversationEndpoints.Map(app);
            WorkEndpoints.Map(app);
        }
        app.MapFallback("/{**path}", () => Results.Json(new ApiFailure(new("not_found", "This endpoint does not exist.")), statusCode: 404));
        return app;
    }
}
