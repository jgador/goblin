using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Application.Work;

public enum IdentityKind
{
    Work,
    Command,
    Conversation,
    Message,
    Attempt,
    Event,
    Inspection,

    [JsonStringEnumMemberName("RepositoryOperation")]
    GitRepositoryOperation
}

public sealed class IdentityRequest
{
    public IdentityRequest(IdentityKind[] kinds)
    {
        Kinds = kinds;
    }

    public IdentityKind[] Kinds { get; init; }
}

public sealed class ReservedIdentities
{
    public ReservedIdentities(long[] ids)
    {
        Ids = ids;
    }

    public long[] Ids { get; init; }
}

// Reserve IDs before constructing core objects or a replayable browser command.
public sealed class IdentityStore
{
    private readonly IDbContextFactory<GoblinDbContext> _dbFactory;

    public IdentityStore(IDbContextFactory<GoblinDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<ReservedIdentities> ReserveAsync(IdentityRequest request, CancellationToken token = default)
    {
        if (request.Kinds is null || request.Kinds.Length is < 1 or > 4 ||
            Array.Exists(request.Kinds, kind => kind is not (IdentityKind.Work or IdentityKind.Command or IdentityKind.Conversation or IdentityKind.Message or IdentityKind.Inspection)))
            throw new ApplicationFailure("invalid_command");
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        long[] ids = new long[request.Kinds.Length];
        for (int i = 0; i < ids.Length; i++) ids[i] = await IdentitySequence.NextAsync(db, request.Kinds[i], token);
        return new(ids);
    }

    public async Task<long> NextEventAsync(CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        return await IdentitySequence.NextAsync(db, IdentityKind.Event, token);
    }
}
