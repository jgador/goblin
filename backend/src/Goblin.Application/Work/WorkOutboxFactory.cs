using System.Collections.Generic;
using System.Linq;
using Goblin.Persistence;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

namespace Goblin.Application.Work;

public sealed class WorkOutboxFactory
{
    private readonly IWolverineRuntime _runtime;
    private readonly IDomainEventScraper[] _scrapers;

    public WorkOutboxFactory(IWolverineRuntime runtime, IEnumerable<IDomainEventScraper> scrapers)
    {
        _runtime = runtime;
        _scrapers = [.. scrapers];
    }

    public IDbContextOutbox Create(GoblinDbContext db)
    {
        // Each operation owns its message buffer as well as its DbContext.
        // A failed or completed operation must not leak messages into the next.
        var outbox = new DbContextOutbox(_runtime, _scrapers);
        outbox.Enroll(db);
        return outbox;
    }
}
