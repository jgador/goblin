using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Goblin.Mcp.Prototype;
using Microsoft.IdentityModel.Tokens;

internal static class HttpsJwksSourceChecks
{
    private const string Issuer = "https://identity.customer.example/realm";
    private static readonly Uri Address = new(Issuer + "/certs");

    public static async Task<int> RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = deadline.Token;
        int checks = 0;

        async Task Pass(string name, Func<Task> run)
        {
            await run();
            checks++;
            Console.WriteLine("PASS " + name);
        }

        async Task Deny(string name, MockHandler handler, Uri? target = null)
        {
            using var source = new CustomerHttpsJwksDocumentSource(Configuration(), handler);
            try { await source.ReadAsync(target ?? Address, token); throw new Exception("Expected key-document denial."); }
            catch (ConnectorAuthenticationFailure error)
            {
                Require(error.InnerException is null && !error.ToString().Contains("PRIVATE", StringComparison.Ordinal));
            }
            Require(handler.Calls == (target is null ? 1 : 0));
            if (handler.SentStream is not null) Require(handler.SentStream.WasDisposed);
            checks++;
            Console.WriteLine("PASS " + name);
        }

        await Pass("production key transport has restrictive settings without network", () =>
        {
            using SocketsHttpHandler handler = CustomerHttpsJwksDocumentSource.CreateHandler(Configuration());
            Require(!handler.AllowAutoRedirect && !handler.UseCookies && !handler.UseProxy && handler.Credentials is null &&
                !handler.PreAuthenticate && handler.AutomaticDecompression == DecompressionMethods.None &&
                handler.ConnectTimeout == TimeSpan.FromSeconds(5) && handler.MaxResponseHeadersLength == 8 &&
                handler.MaxConnectionsPerServer == 2 && handler.SslOptions.RemoteCertificateValidationCallback is null &&
                handler.SslOptions.ClientCertificates is null);
            return Task.CompletedTask;
        });
        await Pass("exact credential-free GET reads chunked UTF-8 and disposes body", async () =>
        {
            var handler = new MockHandler { Bytes = Encoding.UTF8.GetBytes("{\"keys\":[],\"label\":\"公\"}") };
            using var source = new CustomerHttpsJwksDocumentSource(Configuration(), handler);
            Require(handler.Calls == 0);
            string value = await source.ReadAsync(Address, token);
            Require(value.Contains('公') && handler.Calls == 1 && handler.SentStream!.WasDisposed &&
                handler.Method == HttpMethod.Get && handler.Target == Address.OriginalString && handler.CredentialsAbsent);
        });
        await Pass("JWK-set JSON media type is supported", async () =>
        {
            var handler = new MockHandler { MediaType = "application/jwk-set+json" };
            using var source = new CustomerHttpsJwksDocumentSource(Configuration(), handler);
            Require(await source.ReadAsync(Address, token) == "{}");
        });
        foreach (string target in new[] { "http://identity.customer.example/realm/certs", Issuer + "/other", Issuer + "/certs?secret=PRIVATE", "https://foreign.example/certs", Issuer + "/certs/" })
            await Deny("runtime destination cannot change pinned address", new MockHandler(), new Uri(target));
        foreach (string unsafeAddress in new[] { "http://identity.customer.example/certs", "https://user:PRIVATE@identity.customer.example/certs", Address + "?key=PRIVATE", Address + "#PRIVATE", "https://foreign.example/certs" })
            await Pass("unsafe constructor endpoint fails before transport", () =>
            {
                try { using var source = new CustomerHttpsJwksDocumentSource(Configuration(new Uri(unsafeAddress))); throw new Exception("Expected configuration denial."); }
                catch (ArgumentException error) { Require(!error.Message.Contains("PRIVATE", StringComparison.Ordinal)); }
                return Task.CompletedTask;
            });
        foreach (HttpStatusCode status in new[] { HttpStatusCode.MovedPermanently, HttpStatusCode.Redirect, HttpStatusCode.TemporaryRedirect, HttpStatusCode.PermanentRedirect, HttpStatusCode.Unauthorized, HttpStatusCode.InternalServerError, HttpStatusCode.NoContent })
            await Deny("redirect/failure never retries or reads body", new MockHandler { Status = status });
        await Deny("unexpected effective URI is refused", new MockHandler { EffectiveUri = new Uri("https://foreign.example/certs") });
        await Deny("declared oversized body refused before reading", new MockHandler { DeclaredLength = 32769 });
        await Deny("chunked oversized body stops at limit plus one", new MockHandler { Bytes = new byte[40000] });
        await Pass("exact byte limit accepted without extra allocation", async () =>
        {
            var handler = new MockHandler { Bytes = Encoding.UTF8.GetBytes(new string(' ', 32768)) };
            using var source = new CustomerHttpsJwksDocumentSource(Configuration(), handler);
            Require((await source.ReadAsync(Address, token)).Length == 32768 && handler.SentStream!.BytesRead == 32768);
        });
        foreach (var handler in new[]
        {
            new MockHandler { Bytes = [] }, new MockHandler { Bytes = [0xff] },
            new MockHandler { MediaType = "text/html" }, new MockHandler { MediaType = null },
            new MockHandler { Charset = "iso-8859-1" }, new MockHandler { Encoding = "gzip" },
            new MockHandler { ReadFailure = true }, new MockHandler { SendFailure = true }
        }) await Deny("invalid content/provider failure sanitized and disposed", handler);
        await Pass("caller cancellation before send makes no request", async () =>
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var handler = new MockHandler();
            using var source = new CustomerHttpsJwksDocumentSource(Configuration(), handler);
            try { await source.ReadAsync(Address, cancelled.Token); throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
            Require(handler.Calls == 0);
        });
        await Pass("caller cancellation during body preserves cancellation and disposes", async () =>
        {
            using var cancelled = new CancellationTokenSource();
            var handler = new MockHandler { BeforeRead = cancelled.Cancel };
            using var source = new CustomerHttpsJwksDocumentSource(Configuration(), handler);
            try { await source.ReadAsync(Address, cancelled.Token); throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
            Require(handler.Calls == 1 && handler.SentStream!.WasDisposed);
        });
        await Pass("header send has a real bounded deadline", async () =>
        {
            var handler = new MockHandler { BlockSend = true };
            await Deny("header timeout sanitized", handler);
            Require(handler.SendCancelled);
        });
        await Pass("post-header stream has a real bounded deadline", async () =>
        {
            var handler = new MockHandler { BlockRead = true };
            await Deny("body timeout sanitized", handler);
            Require(handler.SentStream!.ReadCancelled);
        });
        await Pass("concurrent cold cache loads one pinned HTTPS document", async () =>
        {
            using RSA rsa = RSA.Create(2048);
            JsonWebKey key = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = "cold" });
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new MockHandler { Bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { keys = new[] { key } })), Release = release.Task };
            using var source = new CustomerHttpsJwksDocumentSource(Configuration(), handler);
            var cache = new CustomerSigningKeyCache(Configuration(), source);
            Task<RsaSecurityKey>[] calls = Enumerable.Range(0, 12).Select(_ => cache.GetAsync("cold", token)).ToArray();
            await handler.Started.Task.WaitAsync(token);
            Require(handler.Calls == 1);
            release.SetResult(true);
            RsaSecurityKey[] results = await Task.WhenAll(calls);
            Require(handler.Calls == 1 && results.All(k => k.KeyId == "cold") && handler.SentStream!.WasDisposed);
        });
        Console.WriteLine($"PASS {checks} HTTPS key-source checks; mocked HTTP/streams only, no DNS/TLS/provider traffic.");
        return checks;
    }

    private static OAuthDiscoverySummary Configuration(Uri? address = null) =>
        new(Issuer, new Uri(Issuer + "/authorize"), new Uri(Issuer + "/token"), address ?? Address, false);

    private static void Require(bool value)
    {
        if (!value) throw new Exception("HTTPS key-source assertion failed.");
    }

    private sealed class MockHandler : HttpMessageHandler
    {
        public byte[] Bytes { get; init; } = System.Text.Encoding.UTF8.GetBytes("{}");
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string? MediaType { get; init; } = "application/json";
        public string? Charset { get; init; }
        public string? Encoding { get; init; }
        public long? DeclaredLength { get; init; }
        public Uri? EffectiveUri { get; init; }
        public bool SendFailure { get; init; }
        public bool ReadFailure { get; init; }
        public bool BlockSend { get; init; }
        public bool BlockRead { get; init; }
        public Action? BeforeRead { get; init; }
        public Task? Release { get; init; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SendCancelled { get; private set; }
        public int Calls { get; private set; }
        public string? Target { get; private set; }
        public HttpMethod? Method { get; private set; }
        public bool CredentialsAbsent { get; private set; }
        public TrackingStream? SentStream { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Target = request.RequestUri!.OriginalString;
            Method = request.Method;
            CredentialsAbsent = request.Content is null && !request.Headers.Contains("Authorization") && !request.Headers.Contains("Cookie") &&
                !request.Headers.Contains("Proxy-Authorization") && request.Headers.Host is null;
            Started.TrySetResult(true);
            if (SendFailure) throw new HttpRequestException("PRIVATE provider configuration");
            try
            {
                if (Release is not null) await Release.WaitAsync(token);
                if (BlockSend) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { SendCancelled = true; throw; }
            SentStream = new TrackingStream(Bytes, BlockRead, ReadFailure, BeforeRead);
            var response = new HttpResponseMessage(Status) { RequestMessage = request, Content = new StreamContent(SentStream) };
            if (EffectiveUri is not null) response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, EffectiveUri);
            if (MediaType is not null) response.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaType) { CharSet = Charset };
            if (Encoding is not null) response.Content.Headers.ContentEncoding.Add(Encoding);
            if (DeclaredLength is not null) response.Content.Headers.ContentLength = DeclaredLength;
            if ((int)Status is >= 300 and < 400) response.Headers.Location = new Uri("https://foreign.example/PRIVATE");
            return response;
        }
    }

    private sealed class TrackingStream : Stream
    {
        private readonly byte[] _bytes;
        private readonly bool _block;
        private readonly bool _fail;
        private readonly Action? _beforeRead;
        public bool WasDisposed { get; private set; }
        public bool ReadCancelled { get; private set; }
        public int BytesRead { get; private set; }

        public TrackingStream(byte[] bytes, bool block, bool fail, Action? beforeRead)
        {
            _bytes = bytes;
            _block = block;
            _fail = fail;
            _beforeRead = beforeRead;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            _beforeRead?.Invoke();
            try
            {
                token.ThrowIfCancellationRequested();
                if (_block) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                if (_fail) throw new IOException("PRIVATE body failure");
                int count = Math.Min(Math.Min(1024, buffer.Length), _bytes.Length - BytesRead);
                _bytes.AsMemory(BytesRead, count).CopyTo(buffer);
                BytesRead += count;
                return count;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { ReadCancelled = true; throw; }
        }

        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
