using System.Security.Cryptography;
using System.Text.Json;
using Goblin.Mcp.Prototype;
using Microsoft.IdentityModel.Tokens;

internal static class SigningKeyInvalidationChecks
{
    public static async Task<int> RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        using RSA original = RSA.Create(2048), replacement = RSA.Create(2048);
        string oldDocument = Document(original, "old"), newDocument = Document(replacement, "new");
        var documents = new Documents { Body = oldDocument };
        var cache = new CustomerSigningKeyCache(Configuration(), documents);
        var owner = new Owner();
        var admin = new CustomerSigningKeyAdministration(cache, owner);
        int checks = 0;

        async Task Check(string name, Func<Task> action)
        {
            await action();
            checks++;
            Console.WriteLine("PASS " + name);
        }

        async Task AuthenticationDenied(Func<Task> action)
        {
            try { await action(); throw new Exception("Expected key denial."); }
            catch (ConnectorAuthenticationFailure error) { Require(error.InnerException is null); }
        }

        async Task OwnerDenied()
        {
            try { await admin.InvalidateAsync(token); throw new Exception("Expected owner denial."); }
            catch (GrantAdministrationFailure error)
            {
                Require(error.Message == "Local owner authorization is required." && error.InnerException is null &&
                    !error.ToString().Contains("PRIVATE", StringComparison.Ordinal));
            }
        }

        await cache.GetAsync("old", token);
        owner.Allowed = false;
        await Check("owner denial does not invalidate cached keys", async () =>
        {
            await OwnerDenied();
            await cache.GetAsync("old", token);
            Require(owner.Calls == 1 && documents.Calls == 1);
        });
        owner.Fail = true;
        await Check("owner provider failure sanitized without cache mutation", async () =>
        {
            await OwnerDenied();
            await cache.GetAsync("old", token);
            Require(documents.Calls == 1);
        });
        owner.Fail = false;
        owner.Allowed = true;
        documents.Body = newDocument;
        await Check("owner invalidation clears keys and permits fresh retrieval without cooldown", async () =>
        {
            int before = documents.Calls;
            await admin.InvalidateAsync(token);
            Require(documents.Calls == before);
            await cache.GetAsync("new", token);
            Require(documents.Calls == before + 1);
            await AuthenticationDenied(() => cache.GetAsync("old", token));
        });
        await Check("owner authority is rechecked on each repeated invalidation", async () =>
        {
            int before = owner.Calls;
            await admin.InvalidateAsync(token);
            await admin.InvalidateAsync(token);
            Require(owner.Calls == before + 2);
        });
        await Check("caller cancellation precedes authority and mutation", async () =>
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            int before = owner.Calls;
            try { await admin.InvalidateAsync(cancelled.Token); throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
            Require(owner.Calls == before);
        });
        await cache.GetAsync("new", token);
        await Check("cancellation after authority prevents cache mutation", async () =>
        {
            using var cancelled = new CancellationTokenSource();
            owner.AfterApproval = cancelled.Cancel;
            int before = documents.Calls;
            try { await admin.InvalidateAsync(cancelled.Token); throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
            finally { owner.AfterApproval = null; }
            await cache.GetAsync("new", token);
            Require(documents.Calls == before);
        });
        await Check("invalidating one installation leaves another cache independent", async () =>
        {
            var otherDocuments = new Documents { Body = oldDocument };
            var other = new CustomerSigningKeyCache(Configuration(), otherDocuments);
            await other.GetAsync("old", token);
            await admin.InvalidateAsync(token);
            await other.GetAsync("old", token);
            Require(otherDocuments.Calls == 1);
        });
        await Check("pre-invalidation refresh cannot republish its captured generation", async () =>
        {
            var blocked = new Documents { Body = oldDocument, Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var racing = new CustomerSigningKeyCache(Configuration(), blocked);
            var raceAdmin = new CustomerSigningKeyAdministration(racing, owner);
            Task<RsaSecurityKey> oldRead = racing.GetAsync("old", token);
            await blocked.Started.Task.WaitAsync(token);
            await raceAdmin.InvalidateAsync(token);
            await raceAdmin.InvalidateAsync(token);
            Require(!blocked.Completed.Task.IsCompleted && blocked.Calls == 1);
            blocked.Release.SetResult(true);
            await AuthenticationDenied(() => oldRead);
            Require(blocked.Completed.Task.IsCompleted && blocked.Calls == 1);
            blocked.Release = null;
            blocked.Body = newDocument;
            await racing.GetAsync("new", token);
            Require(blocked.Calls == 2);
            await AuthenticationDenied(() => racing.GetAsync("old", token));
        });
        await Check("queued reader cancellation and old source cleanup remain bounded", async () =>
        {
            var blocked = new Documents { Body = oldDocument, Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var racing = new CustomerSigningKeyCache(Configuration(), blocked);
            using var cancelled = new CancellationTokenSource();
            Task<RsaSecurityKey> first = racing.GetAsync("old", cancelled.Token);
            await blocked.Started.Task.WaitAsync(token);
            using var queuedCancellation = new CancellationTokenSource();
            Task<RsaSecurityKey> queued = racing.GetAsync("old", queuedCancellation.Token);
            queuedCancellation.Cancel();
            try { await queued; throw new Exception("Expected queued cancellation."); }
            catch (OperationCanceledException) { }
            await new CustomerSigningKeyAdministration(racing, owner).InvalidateAsync(token);
            cancelled.Cancel();
            try { await first; throw new Exception("Expected first cancellation."); }
            catch (OperationCanceledException) { }
            await blocked.Completed.Task.WaitAsync(token);
            Require(blocked.Cancelled && blocked.Calls == 1);
            blocked.Release = null;
            blocked.Body = newDocument;
            await racing.GetAsync("new", token);
            Require(blocked.Calls == 2);
        });
        await Check("failed post-invalidation fetch never falls back to cleared keys", async () =>
        {
            documents.Fail = true;
            await admin.InvalidateAsync(token);
            await AuthenticationDenied(() => cache.GetAsync("new", token));
            int before = documents.Calls;
            await AuthenticationDenied(() => cache.GetAsync("old", token));
            Require(documents.Calls == before);
            documents.Fail = false;
            await admin.InvalidateAsync(token);
            await cache.GetAsync("new", token);
        });
        await Check("owner authorization timeout cancels cooperative authority without mutation", async () =>
        {
            owner.Block = true;
            int before = documents.Calls;
            await OwnerDenied();
            await owner.Completed.Task.WaitAsync(token);
            Require(owner.Cancelled);
            owner.Block = false;
            await cache.GetAsync("new", token);
            Require(documents.Calls == before);
        });
        Console.WriteLine($"PASS {checks} key-invalidation checks; test owner/source, no real session or durable revocation.");
        return checks;
    }

    private static OAuthDiscoverySummary Configuration() => new("https://identity.customer.example/realm",
        new Uri("https://identity.customer.example/realm/authorize"), new Uri("https://identity.customer.example/realm/token"),
        new Uri("https://identity.customer.example/realm/certs"));

    private static string Document(RSA rsa, string kid) => JsonSerializer.Serialize(new
    {
        keys = new[] { JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = kid }) }
    });

    private static void Require(bool value)
    {
        if (!value) throw new Exception("Key-invalidation assertion failed.");
    }

    private sealed class Owner : ILocalOwnerGrantAuthority
    {
        public bool Allowed { get; set; } = true;
        public bool Fail { get; set; }
        public bool Block { get; set; }
        public bool Cancelled { get; private set; }
        public Action? AfterApproval { get; set; }
        public int Calls { get; private set; }
        public TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RequireOwnerAsync(CancellationToken token)
        {
            Calls++;
            if (Fail || !Allowed) throw new InvalidOperationException("PRIVATE owner/session configuration");
            if (Block)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { Cancelled = true; throw; }
                finally { Completed.TrySetResult(true); }
            }
            AfterApproval?.Invoke();
        }
    }

    private sealed class Documents : ICustomerJwksDocumentSource
    {
        public string Body { get; set; } = "{}";
        public bool Fail { get; set; }
        public bool Cancelled { get; private set; }
        public int Calls { get; private set; }
        public TaskCompletionSource<bool>? Release { get; set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> ReadAsync(Uri pinnedAddress, CancellationToken token)
        {
            string captured = Body;
            Calls++;
            Started.TrySetResult(true);
            try
            {
                if (Release is not null) await Release.Task.WaitAsync(token);
                if (Fail) throw new IOException("PRIVATE provider error");
                return captured;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { Cancelled = true; throw; }
            finally { Completed.TrySetResult(true); }
        }
    }
}
