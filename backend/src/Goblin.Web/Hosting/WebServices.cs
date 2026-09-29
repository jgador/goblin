using Env = Goblin.Contracts.Configuration.EnvironmentVariables;
using System;
using System.IO;
using System.Text.Json.Serialization;
using Goblin.Application;
using Goblin.Application.Repositories;
using Goblin.Application.Work;
using Goblin.Application.Workspaces;
using Goblin.Contracts.Runtime;
using Goblin.Execution;
using Goblin.Integrations.Codex;
using Goblin.Integrations.GitHub;
using Goblin.Persistence;
using Goblin.Web.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yarp.ReverseProxy.Forwarder;

namespace Goblin.Web;

internal static class WebServices
{
    public static void Configure(WebApplicationBuilder builder, ApplicationOptions options, Workspace workspace, CodexOptions runtimeOptions)
    {
        builder.Logging.AddGoblinJsonConsole();
        ConfigureMonitoring(builder, options);
        ConfigureProxies(builder, options);
        string? databaseConnection = builder.Configuration.GetConnectionString("Goblin");
        if (!string.IsNullOrWhiteSpace(databaseConnection)) builder.Services.AddGoblinPersistence(databaseConnection);
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        string? executionNamespace = builder.Configuration[Env.GoblinExecutionNamespace];
        builder.Services.AddSingleton(new GitHubConnection(Path.Combine(workspace.DataDirectory, "github-cli"), options.GitHubCommand));
        if (options.EnableWork) ConfigureWork(builder, workspace, options, runtimeOptions, databaseConnection, executionNamespace);
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
    }

    private static void ConfigureMonitoring(WebApplicationBuilder builder, ApplicationOptions options)
    {
        string? nodeName = builder.Configuration[Env.GoblinNodeName];
        if (options.SystemSource is not null) builder.Services.AddSingleton(options.SystemSource);
        else if (!string.IsNullOrWhiteSpace(nodeName))
            builder.Services.AddSingleton<ISystemSource>(_ =>
            {
                string tokenFile = builder.Configuration[Env.GoblinKubernetesTokenFile] ?? "/var/run/secrets/kubernetes.io/serviceaccount/token";
                string caFile = builder.Configuration[Env.GoblinKubernetesCaFile] ?? "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";
                return new KubernetesSystemSource(builder.Configuration[Env.GoblinKubernetesUrl],
                    nodeName, builder.Configuration[Env.GoblinNamespace] ?? "goblin",
                    builder.Configuration[Env.GoblinExecutionNamespace] ?? "agents", tokenFile, caFile);
            });
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
        CodexOptions runtimeOptions, string? databaseConnection, string? executionNamespace)
    {
        if (string.IsNullOrWhiteSpace(databaseConnection)) throw new InvalidOperationException("Durable Work requires PostgreSQL.");
        builder.Host.UseWolverine(messaging => ApplicationServices.ConfigureMessaging(messaging, databaseConnection, !string.IsNullOrWhiteSpace(executionNamespace)));
        builder.Services.AddWorkApplication();
        var workspaceLimits = new WorkspaceLimits(
            builder.Configuration.GetValue(Env.GoblinMaxSandboxes, 2),
            builder.Configuration.GetValue(Env.GoblinMaxCachedWorkspaces, 4));
        if (workspaceLimits.MaxSandboxes < 1 || workspaceLimits.MaxCachedVolumes < 1)
            throw new InvalidOperationException("Invalid workspace capacity configuration.");
        builder.Services.AddSingleton(workspaceLimits);
        builder.Services.AddSingleton<WorkspaceCheckpoints>();
        builder.Services.AddSingleton<IWorkspaceCheckpoints>(services => services.GetRequiredService<WorkspaceCheckpoints>());
        IExecutionHost executionHost = new LocalTextHost(new(
            Path.Combine(workspace.DataDirectory, "executions"), workspace.CodexHome, runtimeOptions.Command,
            typeof(GoblinApplication).Assembly.Location));
        builder.Services.AddSingleton<IRepositoryRemote, GitHubRepositoryRemote>();
        builder.Services.AddSingleton<IRepositoryCatalog>(services => services.GetRequiredService<GitHubConnection>());
        builder.Services.AddSingleton(new RepositoryBrokerOptions(Path.Combine(workspace.DataDirectory, "repositories")));
        builder.Services.AddSingleton<RepositoryBroker>();
        builder.Services.AddSingleton<IRepositoryBroker>(services => services.GetRequiredService<RepositoryBroker>());
        if (!string.IsNullOrWhiteSpace(executionNamespace))
        {
            var kubernetes = new KubernetesApi(builder.Configuration[Env.GoblinKubernetesUrl],
                builder.Configuration[Env.GoblinKubernetesTokenFile] ?? "/var/run/secrets/kubernetes.io/serviceaccount/token",
                builder.Configuration[Env.GoblinKubernetesCaFile] ?? "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt");
            builder.Services.AddSingleton(kubernetes);
            var sandboxOptions = new SandboxOptions(executionNamespace,
                builder.Configuration[Env.GoblinExecutionImage] ?? "goblin-auth:0.1.0", workspace.CodexHome,
                builder.Configuration[Env.GoblinRepositoryUrl] ?? "http://goblin-repository.goblin.svc:8788")
            {
                CpuLimit = builder.Configuration[Env.GoblinSandboxCpuLimit] ?? "2",
                MemoryLimit = builder.Configuration[Env.GoblinSandboxMemoryLimit] ?? "2Gi"
            };
            builder.Services.AddSingleton(sandboxOptions);
            builder.Services.AddSingleton<IInspectionHost, InspectionHost>();
            builder.Services.AddSingleton<InspectionCoordinator>();
            builder.Services.AddHostedService(services => services.GetRequiredService<InspectionCoordinator>());
            builder.Services.AddSingleton<IExecutionHost>(services => options.ExecutionHost ?? new SandboxHost(kubernetes, sandboxOptions,
                executionHost, services.GetRequiredService<IRepositoryBroker>(), services.GetRequiredService<IWorkspaceCheckpoints>(), workspaceLimits));
        }
        else builder.Services.AddSingleton(options.ExecutionHost ?? executionHost);
        builder.Services.AddSingleton<IDispatchFailureJournal>(new FileDispatchFailureJournal(Path.Combine(workspace.DataDirectory, "dispatch-failures")));
    }
}
