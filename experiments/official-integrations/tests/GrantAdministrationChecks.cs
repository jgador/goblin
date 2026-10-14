using Goblin.Mcp.Prototype;

internal static class GrantAdministrationChecks
{
    public static async Task<int> RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clock = new FixedClock();
        var owner = new OwnerAuthority();
        var store = new MutationStore();
        var policy = new CustomerGrantAdministration("https://identity.customer.example/", "https://goblin.customer.example/mcp",
            TimeSpan.FromHours(24), owner, store, clock);
        DateTimeOffset expiry = clock.GetUtcNow().AddHours(24);
        int checks = 0;

        async Task Check(string name, Func<Task> run)
        {
            await run();
            Console.WriteLine("PASS " + name);
            checks++;
        }

        async Task Deny(Func<Task> run, string message)
        {
            int before = store.Calls;
            try { await run(); }
            catch (GrantAdministrationFailure error)
            {
                Require(error.Message == message && error.InnerException is null && store.Calls == before);
                return;
            }
            throw new Exception("Expected grant policy denial.");
        }

        await Check("owner approval creates one exact pinned Work target and UTC expiry", async () =>
        {
            await policy.ApproveAsync("Reviewed-Subject", 9007199254740993, expiry.ToOffset(TimeSpan.FromHours(8)), deadline.Token);
            ConnectorGrantApproval approval = store.Approval!;
            Require(owner.Calls == 1 && store.Calls == 1 && approval.Target.WorkId == 9007199254740993 &&
                approval.Target.Issuer == "https://identity.customer.example/" && approval.Target.Subject == "Reviewed-Subject" &&
                approval.Target.Resource == "https://goblin.customer.example/mcp" && approval.ApprovedAt == clock.GetUtcNow() &&
                approval.ExpiresAt == expiry && approval.ExpiresAt.Offset == TimeSpan.Zero);
        });
        owner.Allowed = false;
        await Check("owner denial precedes approval mutation and input checks", () => Deny(
            () => policy.ApproveAsync("", 0, DateTimeOffset.MinValue, deadline.Token), "Local owner authorization is required."));
        await Check("revocation also requires local owner, not connector identity", () => Deny(
            () => policy.RevokeAsync("Reviewed-Subject", 42, deadline.Token), "Local owner authorization is required."));
        owner.Allowed = true;
        foreach (string subject in new[] { "", " ", "bad\0subject", new string('é', 129), "bad\ud800subject" })
        {
            await Check("invalid or oversized UTF-8 subject cannot reach store", () => Deny(
                () => policy.ApproveAsync(subject, 42, expiry, deadline.Token), "Select one valid subject and positive Work ID."));
        }
        foreach (long id in new[] { 0L, -1L })
            await Check("nonpositive Work ID cannot reach approval store", () => Deny(
                () => policy.ApproveAsync("Reviewed-Subject", id, expiry, deadline.Token), "Select one valid subject and positive Work ID."));
        foreach (DateTimeOffset invalid in new[] { clock.GetUtcNow(), clock.GetUtcNow().AddTicks(-1), expiry.AddTicks(1) })
            await Check("past equal or over-limit expiry cannot reach store", () => Deny(
                () => policy.ApproveAsync("Reviewed-Subject", 42, invalid, deadline.Token), "Select a future expiry within the configured grant lifetime."));
        await Check("boundary UTF-8 subject retained exactly without normalization", async () =>
        {
            string subject = new('é', 128);
            await policy.ApproveAsync(subject, 42, expiry, deadline.Token);
            Require(store.Approval!.Target.Subject == subject);
        });
        await Check("wildcard-looking subject remains one exact subject", async () =>
        {
            await policy.ApproveAsync("*", 42, expiry, deadline.Token);
            Require(store.Approval!.Target.Subject == "*" && store.Approval.Target.WorkId == 42);
        });
        await Check("revocation needs no live connector token and preserves target", async () =>
        {
            await policy.RevokeAsync("Reviewed-Subject", 42, deadline.Token);
            Require(store.Revocation!.Subject == "Reviewed-Subject" && store.Revocation.WorkId == 42 && store.RevokedAt == clock.GetUtcNow());
        });
        await Check("owner authorization is rechecked on every operation", () =>
        {
            Require(owner.Calls == 16);
            return Task.CompletedTask;
        });
        store.Unavailable = true;
        foreach (Func<Task> run in new Func<Task>[]
        {
            () => policy.ApproveAsync("Reviewed-Subject", 42, expiry, deadline.Token),
            () => policy.RevokeAsync("Reviewed-Subject", 42, deadline.Token)
        })
            await Check("unconfirmed store mutation is sanitized and never retried", async () =>
            {
                int before = store.Calls;
                try { await run(); }
                catch (GrantAdministrationFailure error)
                {
                    Require(error.Message == "Grant change could not be confirmed." && error.InnerException is null && store.Calls == before + 1);
                    return;
                }
                throw new Exception("Expected unconfirmed grant mutation.");
            });
        await Check("owner authority failure is sanitized before store access", async () =>
        {
            owner.Unavailable = true;
            await Deny(() => policy.ApproveAsync("Reviewed-Subject", 42, expiry, deadline.Token), "Local owner authorization is required.");
            owner.Unavailable = false;
        });
        await Check("caller cancellation stops before owner or store", async () =>
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            int before = owner.Calls;
            try { await policy.RevokeAsync("Reviewed-Subject", 42, canceled.Token); }
            catch (OperationCanceledException) { Require(owner.Calls == before); return; }
            throw new Exception("Expected caller cancellation.");
        });
        foreach (TimeSpan invalid in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromDays(7).Add(TimeSpan.FromTicks(1)) })
            await Check("grant lifetime configuration is explicitly bounded", () =>
            {
                try { _ = new CustomerGrantAdministration("https://identity.customer.example/", "https://goblin.customer.example/mcp", invalid, owner, store, clock); }
                catch (ArgumentException) { return Task.CompletedTask; }
                throw new Exception("Expected invalid grant lifetime.");
            });
        Console.WriteLine($"PASS {checks} grant administration policy checks; test owner authority/store, no live session or durable mutation.");
        return checks;
    }

    private static void Require(bool value)
    {
        if (!value) throw new Exception("Grant administration assertion failed.");
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 14, 1, 0, 0, TimeSpan.Zero);
    }

    private sealed class OwnerAuthority : ILocalOwnerGrantAuthority
    {
        public bool Allowed { get; set; } = true;
        public bool Unavailable { get; set; }
        public int Calls { get; private set; }

        public Task RequireOwnerAsync(CancellationToken token)
        {
            Calls++;
            if (!Allowed || Unavailable) throw new InvalidOperationException("PRIVATE local session detail");
            return Task.CompletedTask;
        }
    }

    private sealed class MutationStore : ICustomerGrantMutationStore
    {
        public int Calls { get; private set; }
        public bool Unavailable { get; set; }
        public ConnectorGrantApproval? Approval { get; private set; }
        public ConnectorGrantTarget? Revocation { get; private set; }
        public DateTimeOffset RevokedAt { get; private set; }

        public Task ApproveWithAuditAsync(ConnectorGrantApproval approval, CancellationToken token)
        {
            Calls++;
            if (Unavailable) throw new InvalidOperationException("PRIVATE database detail");
            Approval = approval;
            return Task.CompletedTask;
        }

        public Task RevokeWithAuditAsync(ConnectorGrantTarget target, DateTimeOffset revokedAt, CancellationToken token)
        {
            Calls++;
            if (Unavailable) throw new InvalidOperationException("PRIVATE database detail");
            Revocation = target;
            RevokedAt = revokedAt;
            return Task.CompletedTask;
        }
    }
}
