using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Connections;
using Goblin.Application.Work;
using Goblin.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goblin.Application.Tests;

public sealed partial class DurabilityTests
{
    [DatabaseFact]
    public async Task PluginPreparationUsesTheConnectionReservationAndCancellationReleasesIt()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        var authentication = new CodexAuthentication();
        ConnectionStore store = fixture.Host.Services.GetRequiredService<ConnectionStore>();
        var service = new ConnectionService(authentication, store, fixture.Host.Services.GetRequiredService<ModelCatalogStore>());
        using var cancellation = new CancellationTokenSource();
        Task<ChatGptPluginDraft> preparation = service.PreparePluginAsync(new(
            "https://goblin.example.test/mcp", "https://goblin.example.test/", "goblin-chatgpt", "goblin.work.read", null), cancellation.Token);
        await authentication.PromptEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ConnectionAvailability.Verifying, (await store.ListAsync()).Single().Availability);
        await Assert.ThrowsAsync<ApplicationFailure>(() => service.CheckPluginCreatorAsync(default));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
        Assert.NotEqual(ConnectionAvailability.Verifying, (await store.ListAsync()).Single().Availability);
        Assert.False((await service.CheckPluginCreatorAsync(default)).SkillAvailable);
        Assert.Equal(ConnectionAvailability.Available, (await store.ListAsync()).Single().Availability);
        Assert.Equal(1, authentication.PromptCalls);
    }

    [DatabaseFact]
    public async Task PluginPreparationPrerequisiteFailurePreservesAnAvailableApiKeyConnection()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        var authentication = new CodexAuthentication { Account = new ApiKeyAccountView() };
        ConnectionStore store = fixture.Host.Services.GetRequiredService<ConnectionStore>();
        var service = new ConnectionService(authentication, store, fixture.Host.Services.GetRequiredService<ModelCatalogStore>());
        ApplicationFailure error = await Assert.ThrowsAsync<ApplicationFailure>(() => service.CheckPluginCreatorAsync(default));
        Assert.Equal("plugin_chatgpt_login_required", error.Code);
        Assert.Equal(ConnectionAvailability.Available, (await store.ListAsync()).Single().Availability);
        Assert.Equal(0, authentication.PromptCalls);
    }
}
