using System;
using System.IO;
using System.Text.Json.Serialization;
using Goblin.Application;
using Goblin.Application.GitRepositories;
using Goblin.Application.Work;
using Goblin.Application.Workspaces;
using Goblin.Contracts;
using Goblin.Contracts.Conversations;
using Goblin.Contracts.Runtime;
using Goblin.Execution;
using Goblin.Integrations.Codex;
using Goblin.Integrations.GitHub;
using Goblin.Integrations.Slack;
using Goblin.Persistence;
using Goblin.Web.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yarp.ReverseProxy.Forwarder;

namespace Goblin.Web;

internal static class WebServices
{
    public static WebRuntimeConfiguration Configure(WebApplicationBuilder builder, ApplicationOptions options,
        Workspace workspace, CodexOptions runtimeOptions)
    {
        WebRuntimeConfiguration configuration = WebRuntimeConfiguration.Read(builder.Configuration, options.EnableWork);
        builder.Logging.AddGoblinJsonConsole();
        ConfigureMonitoring(builder, options, configuration);
        ConfigureProxies(builder, options);
        if (!string.IsNullOrWhiteSpace(configuration.DatabaseConnection))
            builder.Services.AddGoblinPersistence(configuration.DatabaseConnection);
        builder.Services.ConfigureHttpJsonOptions(json => GitRepositoryJson.Configure(json.SerializerOptions));
        builder.Services.AddSingleton(new GitHubConnection(Path.Combine(workspace.DataDirectory, "github-cli"), options.GitHubCommand));
        if (options.EnableWork) ConfigureWork(builder, workspace, options, runtimeOptions, configuration);
        if (options.EnableWork)
        {
            builder.Services.AddSingleton<SlackApi>();
            builder.Services.AddSingleton(new SlackCredentialStore(Path.Combine(workspace.DataDirectory, "slack")));
            builder.Services.AddSingleton<IExternalConversations, ExternalConversationAdapter>();
            builder.Services.AddSingleton(services => new SlackConnection(services.GetRequiredService<SlackApi>(),
                services.GetRequiredService<SlackCredentialStore>(), services.GetRequiredService<IExternalConversations>(), options.PublicOrigin));
            builder.Services.AddHostedService(services => services.GetRequiredService<SlackConnection>());
            builder.Services.AddSingleton(services => new SlackSetup(Path.Combine(workspace.DataDirectory, "slack-setup"),
                services.GetRequiredService<SlackApi>(), services.GetRequiredService<SlackConnection>()));
        }
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            server.Limits.MaxRequestHeaderCount = 32;
            server.Limits.MaxRequestBodySize = null; // ReadBodyAsync enforces the limit even for chunked input.
        });
        builder.Services.AddSingleton(workspace);
        builder.Services.AddSingleton(new WorkspacePreferences(workspace.DataDirectory));
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
        return configuration;
    }

    private static void ConfigureMonitoring(WebApplicationBuilder builder, ApplicationOptions options,
        WebRuntimeConfiguration configuration)
    {
        if (options.SystemSource is not null) builder.Services.AddSingleton(options.SystemSource);
        else if (!string.IsNullOrWhiteSpace(configuration.NodeName))
            builder.Services.AddSingleton<ISystemSource>(_ => new KubernetesSystemSource(configuration.KubernetesUrl,
                configuration.NodeName, configuration.KubernetesNamespace, configuration.MonitoringExecutionNamespace,
                configuration.KubernetesTokenFile, configuration.KubernetesCaFile));
        builder.Services.AddSingleton(services => new SystemMonitor(services.GetService<ISystemSource>()));
        builder.Services.AddHostedService(services => services.GetRequiredService<SystemMonitor>());
    }

    private static void ConfigureProxies(WebApplicationBuilder builder, ApplicationOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.HeadlampUrl))
        {
            builder.Services.AddHttpForwarder();
            builder.Services.AddSingleton(services => new HeadlampProxy(services.GetRequiredService<IHttpForwarder>(), options.HeadlampUrl));
        }
        if (!string.IsNullOrWhiteSpace(options.VictoriaLogsUrl))
        {
            builder.Services.AddHttpForwarder();
            builder.Services.AddSingleton(services => new VictoriaLogsProxy(services.GetRequiredService<IHttpForwarder>(), options.VictoriaLogsUrl,
                services.GetRequiredService<WorkspacePreferences>()));
        }
    }

    private static void ConfigureWork(WebApplicationBuilder builder, Workspace workspace, ApplicationOptions options,
        CodexOptions runtimeOptions, WebRuntimeConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.DatabaseConnection))
            throw new InvalidOperationException("Durable Work requires PostgreSQL.");
        builder.Host.UseWolverine(messaging => ApplicationServices.ConfigureMessaging(messaging,
            configuration.DatabaseConnection, !string.IsNullOrWhiteSpace(configuration.ExecutionNamespace)));
        builder.Services.AddWorkApplication();
        WorkspaceLimits workspaceLimits = configuration.WorkspaceLimits;
        builder.Services.AddSingleton(workspaceLimits);
        builder.Services.AddSingleton<WorkspaceCheckpoints>();
        builder.Services.AddSingleton<IWorkspaceCheckpoints>(services => services.GetRequiredService<WorkspaceCheckpoints>());
        IExecutionHost executionHost = new LocalTextHost(new(
            Path.Combine(workspace.DataDirectory, "executions"), workspace.CodexHome, runtimeOptions.Command,
            typeof(GoblinApplication).Assembly.Location));
        builder.Services.AddSingleton<IGitRepositoryRemote, GitHubRepositoryRemote>();
        builder.Services.AddSingleton<IGitRepositoryCatalog>(services => services.GetRequiredService<GitHubConnection>());
        builder.Services.AddSingleton(new GitRepositoryBrokerOptions(Path.Combine(workspace.DataDirectory, "repositories")));
        builder.Services.AddSingleton<GitRepositoryBroker>();
        builder.Services.AddSingleton<IGitRepositoryBroker>(services => services.GetRequiredService<GitRepositoryBroker>());
        if (!string.IsNullOrWhiteSpace(configuration.ExecutionNamespace))
        {
            var kubernetes = new KubernetesApi(configuration.KubernetesUrl,
                configuration.KubernetesTokenFile, configuration.KubernetesCaFile);
            builder.Services.AddSingleton(kubernetes);
            var sandboxOptions = new SandboxOptions(configuration.ExecutionNamespace,
                configuration.ExecutionImage, workspace.CodexHome, configuration.GitRepositoryUrl)
            {
                CpuLimit = configuration.SandboxCpuLimit,
                MemoryLimit = configuration.SandboxMemoryLimit
            };
            builder.Services.AddSingleton(sandboxOptions);
            builder.Services.AddSingleton<IInspectionHost, InspectionHost>();
            builder.Services.AddSingleton<InspectionCoordinator>();
            builder.Services.AddHostedService(services => services.GetRequiredService<InspectionCoordinator>());
            builder.Services.AddSingleton<IExecutionHost>(services => options.ExecutionHost ?? new SandboxHost(kubernetes, sandboxOptions,
                executionHost, services.GetRequiredService<IGitRepositoryBroker>(), services.GetRequiredService<IWorkspaceCheckpoints>(), workspaceLimits));
        }
        else builder.Services.AddSingleton(options.ExecutionHost ?? executionHost);
        builder.Services.AddSingleton<IDispatchFailureJournal>(new FileDispatchFailureJournal(Path.Combine(workspace.DataDirectory, "dispatch-failures")));
    }
}
