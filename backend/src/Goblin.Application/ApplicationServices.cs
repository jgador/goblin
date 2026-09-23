using Goblin.Application.Repositories;
using Goblin.Application.Runtime;
using Goblin.Application.Work;
using Goblin.Application.Workspaces;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;

namespace Goblin.Application;

public static class ApplicationServices
{
    public static void ConfigureMessaging(WolverineOptions options, string connection, bool inspectionEnabled = false)
    {
        options.PersistMessagesWithPostgresql(connection, "public").OverrideAutoCreateResources(AutoCreate.None);
        options.AutoBuildMessageStorageOnStartup = AutoCreate.None;
        options.UseEntityFrameworkCoreTransactions();
        options.Discovery.DisableConventionalDiscovery();
        options.Discovery.IncludeType(typeof(DispatchWorkHandler));
        options.Discovery.IncludeType(typeof(ReconcileWorkHandler));
        options.Discovery.IncludeType(typeof(PublishRepositoryHandler));
        if (inspectionEnabled) options.Discovery.IncludeType(typeof(InspectionHandler));
        options.LocalQueue("work").UseDurableInbox();
        options.PublishMessage<DispatchWork>().ToLocalQueue("work");
        options.PublishMessage<ReconcileWork>().ToLocalQueue("work");
        options.PublishMessage<PublishRepository>().ToLocalQueue("work");
        options.PublishMessage<StartInspection>().ToLocalQueue("work");
        options.PublishMessage<StopInspection>().ToLocalQueue("work");
        options.Policies.OnException<System.Exception>().MoveToErrorQueue();
    }

    public static void AddWorkApplication(this IServiceCollection services)
    {
        services.AddScoped<WorkOutboxFactory>();
        services.AddScoped<IdentityStore>();
        services.AddScoped<WorkStore>();
        services.AddScoped<WorkMemoryRetriever>();
        services.AddScoped<InspectionStore>();
        services.AddScoped<GitHubStore>();
        services.AddScoped<ConversationStore>();
        services.AddSingleton<ExecutionCoordinator>();
        services.AddHostedService<WorkRecovery>();
    }
}
