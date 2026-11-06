using System;
using System.Linq;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goblin.Application.Tests;

public sealed partial class DurabilityTests
{
    [DatabaseFact]
    public async Task WorkCreatedBeforeAndAfterAConnectionIsListedAndEditableWithoutGrants()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        long firstId = NextId();
        long secondId = NextId();
        await fixture.Apply(WorkCommands.Create(NextId(), firstId, "Pre-existing full objective"));
        WorkStore store = fixture.Host.Services.GetRequiredService<WorkStore>();
        Assert.Equal(firstId, Assert.Single(await store.ListPageAsync(0, 25)).Work.Id);

        await fixture.Apply(WorkCommands.Create(NextId(), secondId, "New full objective"));
        WorkView[] page = await store.ListPageAsync(0, 1);
        Assert.Equal(firstId, Assert.Single(page).Work.Id);
        Assert.Equal(secondId, Assert.Single(await store.ListPageAsync(firstId, 1)).Work.Id);
        WorkView updated = await fixture.Apply(WorkCommands.AddContext(NextId(), firstId, (await fixture.Get(firstId)).Version,
            "Context from the connection"));
        Assert.Contains(updated.Work.Messages, message => message.Text == "Context from the connection");
        await fixture.RestartAsync();
        WorkView persisted = await fixture.Get(firstId);
        Assert.Equal("Pre-existing full objective", persisted.Work.Objective);
        Assert.Contains(persisted.Work.Messages, message => message.Text == "Context from the connection");
        Assert.Equal("New full objective", (await fixture.Get(secondId)).Work.Objective);
    }

    [DatabaseFact]
    public async Task RepeatedCreateAndExecutionCommandsReturnOneDurableReceipt()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        long workId = NextId();
        WorkCommand create = WorkCommands.Create(NextId(), workId, "Once only");
        WorkView first = await fixture.Apply(create);
        WorkView replay = await fixture.Apply(create);
        Assert.Equal(first.Version, replay.Version);
        Assert.Equal(workId, Assert.Single(await fixture.Host.Services.GetRequiredService<WorkStore>().ListPageAsync(0, 25)).Work.Id);
        await Assert.ThrowsAsync<ApplicationFailure>(() => fixture.Apply(WorkCommands.Create(create.CommandId, NextId(), "Different command")));

        WorkCommand execute = WorkCommands.Execute(NextId(), workId, first.Version);
        WorkView accepted = await fixture.Apply(execute);
        WorkView duplicate = await fixture.Apply(execute);
        Assert.Equal(accepted.Work.Attempts.Select(a => a.Id), duplicate.Work.Attempts.Select(a => a.Id));
        Assert.Single(accepted.Work.Attempts);
        await Assert.ThrowsAsync<ApplicationFailure>(() => fixture.Apply(WorkCommands.AddContext(NextId(), workId, first.Version, "Stale")));
    }
}
