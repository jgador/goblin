using Goblin.Application.Runtime;
using Goblin.Application.Work;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;

namespace Goblin.Application;

public static class ApplicationServices
{
    public static void ConfigureMessaging(WolverineOptions options, string connection)
    {
        options.PersistMessagesWithPostgresql(connection, "public").OverrideAutoCreateResources(AutoCreate.None);
        options.AutoBuildMessageStorageOnStartup = AutoCreate.None;
        options.UseEntityFrameworkCoreTransactions();
        options.Discovery.IncludeAssembly(typeof(DispatchWorkHandler).Assembly);
        options.LocalQueue("work").UseDurableInbox();
        options.PublishMessage<DispatchWork>().ToLocalQueue("work");
        options.PublishMessage<ReconcileWork>().ToLocalQueue("work");
        options.Policies.OnException<System.Exception>().MoveToErrorQueue();
    }

    public static void AddWorkApplication(this IServiceCollection services)
    {
        services.AddScoped<WorkStore>();
        services.AddScoped<ConversationStore>();
        services.AddSingleton<ExecutionCoordinator>();
        services.AddHostedService<WorkRecovery>();
    }
}
