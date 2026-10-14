using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Goblin.Mcp.Prototype;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

internal static class HttpsKeyCancellationChecks
{
    private const string Issuer = "https://identity.customer.example/realm";
    private const string Resource = "https://goblin.customer.example/mcp";
    private static readonly Uri Address = new(Issuer + "/certs");

    public static async Task<int> RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        using RSA key = RSA.Create(2048);
        string document = JsonSerializer.Serialize(new
        {
            keys = new[] { JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(key.ExportParameters(false)) { KeyId = "current" }) }
        });
        string credential = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = Resource, TokenType = "at+jwt",
            Claims = new Dictionary<string, object> { ["sub"] = "linked", ["jti"] = "ephemeral", ["scope"] = ConnectorReadBoundary.ReadScope },
            NotBefore = DateTime.UtcNow.AddMinutes(-1), Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = "current" }, SecurityAlgorithms.RsaSha256)
        });
        var configuration = new OAuthDiscoverySummary(Issuer, new Uri(Issuer + "/authorize"), new Uri(Issuer + "/token"), Address, false);
        using var handler = new CancellationHandler(document);
        using var documents = new CustomerHttpsJwksDocumentSource(configuration, handler);
        var clock = new CacheClock();
        var source = new LocalStatus();
        var boundary = new RotatingConnectorReadBoundary(new CustomerSigningKeyCache(configuration, documents, clock), Resource, source);
        int checks = 0;

        async Task Pass(string name, Func<Task> action)
        {
            await action();
            checks++;
            Console.WriteLine("PASS " + name);
        }

        await Pass("in-flight key body caller cancellation propagates without Work and disposes request response stream", async () =>
        {
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<WorkStatusResult> operation = boundary.ReadAsync(credential, 42, caller.Token);
            try
            {
                await handler.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Require(!operation.IsCompleted && handler.Calls == 1 && source.Calls == 0);
                caller.Cancel();
                try { await operation.WaitAsync(TimeSpan.FromSeconds(5), token); throw new Exception("Expected caller cancellation."); }
                catch (OperationCanceledException error) when (caller.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    Require(error.InnerException is null && !error.ToString().Contains(credential, StringComparison.Ordinal));
                }
                // Cache WaitAsync may complete before the source finishes unwinding.
                // Wait for actual request disposal, not just a cancelled outer task.
                await handler.Requests[0].Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Require(handler.Bodies[0].ReadCancelled && handler.Bodies[0].WasDisposed && handler.Responses[0].WasDisposed &&
                    handler.SafeRequests && handler.Calls == 1 && source.Calls == 0 && source.Reads == 0);
            }
            finally
            {
                caller.Cancel();
                try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
            }
        });
        await Pass("explicit caller after cancelled retrieval remains denied inside controlled cooldown", async () =>
        {
            try { await boundary.AuthenticateAsync(credential, token).WaitAsync(TimeSpan.FromSeconds(5), token); throw new Exception("Expected cooldown denial."); }
            catch (ConnectorAuthenticationFailure error) { Require(error.InnerException is null && !error.ToString().Contains(credential, StringComparison.Ordinal)); }
            Require(handler.Calls == 1 && source.Calls == 0 && source.Reads == 0);
        });
        await Pass("separate authentication after controlled cooldown proves cancelled cache gate released", async () =>
        {
            handler.AllowRecovery();
            clock.Advance(TimeSpan.FromSeconds(61));
            RotatingConnectorIdentity identity = await boundary.AuthenticateAsync(credential, token).WaitAsync(TimeSpan.FromSeconds(5), token);
            await handler.Requests[1].Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Require(identity.Issuer == Issuer && identity.Subject == "linked" && identity.Resource == Resource &&
                handler.Calls == 2 && handler.SafeRequests && handler.Bodies[1].WasDisposed && handler.Responses[1].WasDisposed &&
                source.Calls == 0 && source.Reads == 0);
        });
        await Pass("recovered cancellation composition still checks local Work permission before projection", async () =>
        {
            try { await boundary.ReadAsync(credential, 42, token); throw new Exception("Expected local permission denial."); }
            catch (WorkReadFailure error) { Require(!error.ToString().Contains(credential, StringComparison.Ordinal)); }
            Require(source.Calls == 1 && source.Reads == 0 && handler.Calls == 2);
        });
        await Pass("explicit restored permission permits separate cached read after cancelled key retrieval", async () =>
        {
            source.Denied = false;
            WorkStatusResult result = await boundary.ReadAsync(credential, 42, token);
            Require(result.WorkId == "42" && result.Version == "9007199254740993" && result.Status == "Ready" &&
                result.UpdatedAt == source.UpdatedAt && source.Calls == 2 && source.Reads == 1 && handler.Calls == 2);
        });
        documents.Dispose();
        await Pass("cancelled key composition disposes source-owned handler and both observed exchanges", () =>
        {
            Require(handler.WasDisposed && handler.Calls == 2 && handler.Requests.All(request => request.WasDisposed) &&
                handler.Responses.All(response => response.WasDisposed) && handler.Bodies.All(body => body.WasDisposed));
            return Task.CompletedTask;
        });
        await RunWaitingCallerAsync(document, credential, configuration, token, Pass);
        Console.WriteLine($"PASS {checks} in-flight key cancellation checks; actual source/cache/JWT, mocked streams and Work only.");
        return checks;
    }

    private static async Task RunWaitingCallerAsync(string document, string credential, OAuthDiscoverySummary configuration,
        CancellationToken token, Func<string, Func<Task>, Task> pass)
    {
        using var handler = new CancellationHandler(document);
        using var documents = new CustomerHttpsJwksDocumentSource(configuration, handler);
        var source = new LocalStatus();
        var boundary = new RotatingConnectorReadBoundary(new CustomerSigningKeyCache(configuration, documents, new CacheClock()), Resource, source);
        using var firstCaller = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var waiter = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<RotatingConnectorIdentity> first = boundary.AuthenticateAsync(credential, firstCaller.Token);
        Task<RotatingConnectorIdentity>? queued = null;
        try
        {
            await handler.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            await pass("queued caller cancellation leaves the single ongoing key body and first caller intact", async () =>
            {
                queued = boundary.AuthenticateAsync(credential, waiter.Token);
                Require(!queued.IsCompleted && !first.IsCompleted && handler.Calls == 1 && source.Calls == 0);
                waiter.Cancel();
                try { await queued.WaitAsync(TimeSpan.FromSeconds(5), token); throw new Exception("Expected queued caller cancellation."); }
                catch (OperationCanceledException error) when (waiter.IsCancellationRequested && !firstCaller.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    Require(error.InnerException is null && !error.ToString().Contains(credential, StringComparison.Ordinal));
                }
                Require(queued.IsCanceled && !first.IsCompleted && !firstCaller.IsCancellationRequested &&
                    handler.Calls == 1 && handler.SafeRequests && !handler.Bodies[0].ReadCancelled && !handler.Bodies[0].WasDisposed &&
                    !handler.Responses[0].WasDisposed && !handler.Requests[0].WasDisposed && source.Calls == 0 && source.Reads == 0);
            });
            await pass("explicit body release authenticates the original caller without another GET or Work access", async () =>
            {
                handler.BodyRelease.TrySetResult(true);
                RotatingConnectorIdentity identity = await first.WaitAsync(TimeSpan.FromSeconds(5), token);
                await handler.Requests[0].Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Require(identity.Issuer == Issuer && identity.Subject == "linked" && identity.Resource == Resource &&
                    handler.Calls == 1 && handler.SafeRequests && !handler.Bodies[0].ReadCancelled && handler.Bodies[0].WasDisposed &&
                    handler.Responses[0].WasDisposed && queued?.IsCanceled == true && source.Calls == 0 && source.Reads == 0);
            });
            await pass("cached authentication after queued cancellation still cannot bypass local Work denial", async () =>
            {
                RotatingConnectorIdentity fresh = await boundary.AuthenticateAsync(credential, token).WaitAsync(TimeSpan.FromSeconds(5), token);
                Require(!ReferenceEquals(fresh, await first) && fresh.Subject == "linked" && handler.Calls == 1 && source.Calls == 0);
                try { await boundary.ReadAsync(credential, 42, token); throw new Exception("Expected local permission denial."); }
                catch (WorkReadFailure error) { Require(!error.ToString().Contains(credential, StringComparison.Ordinal)); }
                Require(source.Calls == 1 && source.Reads == 0 && handler.Calls == 1 && queued?.IsCanceled == true);
            });
            await pass("explicit permission restoration permits independent cached read after queued cancellation", async () =>
            {
                source.Denied = false;
                WorkStatusResult result = await boundary.ReadAsync(credential, 42, token);
                Require(result.WorkId == "42" && result.Version == "9007199254740993" && result.Status == "Ready" &&
                    result.UpdatedAt == source.UpdatedAt && source.Calls == 2 && source.Reads == 1 && handler.Calls == 1 && queued?.IsCanceled == true);
            });
        }
        finally
        {
            waiter.Cancel();
            firstCaller.Cancel();
            handler.BodyRelease.TrySetResult(true);
            try { await first.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (firstCaller.IsCancellationRequested) { }
            if (queued is not null)
            {
                try { await queued.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (waiter.IsCancellationRequested) { }
            }
        }
        documents.Dispose();
        await pass("queued cancellation composition disposes owned handler and its single observed exchange", () =>
        {
            Require(handler.WasDisposed && handler.Calls == 1 && handler.Requests.Count == 1 && handler.Responses.Count == 1 &&
                handler.Bodies.Count == 1 && handler.Requests[0].WasDisposed && handler.Responses[0].WasDisposed && handler.Bodies[0].WasDisposed);
            return Task.CompletedTask;
        });
    }

    private static void Require(bool value)
    {
        if (!value) throw new Exception("In-flight key cancellation assertion failed.");
    }

    private sealed class CacheClock : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        private readonly byte[] _document;
        private bool _recover;
        public TaskCompletionSource<bool> BodyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> BodyRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<RequestProbe> Requests { get; } = [];
        public List<OwnedResponse> Responses { get; } = [];
        public List<BodyStream> Bodies { get; } = [];
        public int Calls { get; private set; }
        public bool SafeRequests { get; private set; } = true;
        public bool WasDisposed { get; private set; }

        public CancellationHandler(string document) { _document = Encoding.UTF8.GetBytes(document); }

        public void AllowRecovery()
        {
            Require(Calls == 1 && Requests[0].WasDisposed && Responses[0].WasDisposed && Bodies[0].WasDisposed);
            _recover = true;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            SafeRequests &= request.Method == HttpMethod.Get && request.RequestUri?.OriginalString == Address.OriginalString &&
                request.Content is null && request.Headers.All(header => header.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)) &&
                request.Headers.Accept.Select(value => value.MediaType).SequenceEqual(new[] { "application/jwk-set+json", "application/json" });
            Require(SafeRequests && !WasDisposed && Calls == (_recover ? 2 : 1));
            // Friend-only disposal sentinel, attached AFTER validating the real
            // bodyless GET. This handler never sends or serializes it anywhere.
            var probe = new RequestProbe();
            request.Content = probe;
            Requests.Add(probe);
            var body = new BodyStream(_document, !_recover, BodyStarted, BodyRelease.Task);
            Bodies.Add(body);
            var response = new OwnedResponse { RequestMessage = request, Content = new StreamContent(body) };
            response.Content.Headers.ContentType = new("application/jwk-set+json");
            Responses.Add(response);
            return Task.FromResult<HttpResponseMessage>(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class RequestProbe : ByteArrayContent
    {
        public bool WasDisposed { get; private set; }
        public TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RequestProbe() : base([]) { }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { WasDisposed = true; Disposed.TrySetResult(true); }
        }
    }

    private sealed class OwnedResponse : HttpResponseMessage
    {
        public bool WasDisposed { get; private set; }

        public OwnedResponse() : base(HttpStatusCode.OK) { }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) WasDisposed = true;
        }
    }

    private sealed class BodyStream : MemoryStream
    {
        private readonly bool _pause;
        private readonly TaskCompletionSource<bool> _started;
        private readonly Task _release;
        public bool WasDisposed { get; private set; }
        public bool ReadCancelled { get; private set; }

        public BodyStream(byte[] document, bool pause, TaskCompletionSource<bool> started, Task release) : base(document, writable: false)
        {
            _pause = pause;
            _started = started;
            _release = release;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_pause)
            {
                _started.TrySetResult(true);
                try { await _release.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { ReadCancelled = true; throw; }
            }
            return await base.ReadAsync(buffer, token);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class LocalStatus : ICustomerWorkStatusSource
    {
        public bool Denied { get; set; } = true;
        public int Calls { get; private set; }
        public int Reads { get; private set; }
        public DateTimeOffset UpdatedAt { get; } = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);

        public Task<WorkStatusResult> ReadAuthorizedAsync(ConnectorIdentity identity, long workId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            if (Denied || identity.Issuer != Issuer || identity.Subject != "linked" || identity.Resource != Resource || workId != 42)
                throw new WorkReadFailure("Connector Work read access is not authorized.");
            Reads++;
            return Task.FromResult(new WorkStatusResult("42", "9007199254740993", "Ready", UpdatedAt));
        }
    }
}
