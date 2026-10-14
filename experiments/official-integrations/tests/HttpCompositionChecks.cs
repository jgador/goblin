using System.Net;
using System.Security.Cryptography;
using Goblin.Mcp.Http.Prototype;
using Goblin.Mcp.Prototype;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

internal static class HttpCompositionChecks
{
    private const string Failure = "Configure and map exactly one private MCP boundary instance per host.";
    private const string Issuer = "https://identity.customer.example/realm";
    private const string Resource = "https://goblin.customer.example/mcp";

    public static async Task<int> RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using RSA rsa = RSA.Create(2048);
        int checks = 0;

        async Task Check(string name, Func<Task> action)
        {
            await action();
            checks++;
            Console.WriteLine("PASS " + name);
        }

        foreach (bool rotating in new[] { false, true })
        {
            var selected = new Configuration(rsa, rotating);
            var equivalent = new Configuration(rsa, rotating);
            var opposite = new Configuration(rsa, !rotating);
            string path = rotating ? "rotating" : "fixed";
            foreach (Configuration duplicate in new[] { selected, equivalent, opposite })
                await Check(path + " duplicate or mixed AddServices rejects without registry changes", () =>
                {
                    var services = new ServiceCollection();
                    selected.Add(services);
                    ServiceDescriptor[] before = services.ToArray();
                    Deny(() => duplicate.Add(services));
                    Require(before.SequenceEqual(services));
                    return Task.CompletedTask;
                });
            foreach (Configuration existing in new[] { selected, opposite })
                await Check(path + " pre-registered boundary cannot bypass composition ownership", () =>
                {
                    var services = new ServiceCollection();
                    existing.AddBoundary(services);
                    ServiceDescriptor[] before = services.ToArray();
                    Deny(() => selected.Add(services));
                    Require(before.SequenceEqual(services));
                    return Task.CompletedTask;
                });
            await Check(path + " Map without adapter registration adds no routes or authentication middleware", async () =>
            {
                WebApplicationBuilder builder = Builder();
                await using WebApplication app = builder.Build();
                Deny(() => selected.Map(app));
                Require(((IEndpointRouteBuilder)app).DataSources.Count == 0);
                await app.StartAsync(deadline.Token);
                using HttpClient client = app.GetTestClient();
                client.BaseAddress = new Uri("https://goblin.customer.example/");
                client.Timeout = TimeSpan.FromSeconds(5);
                using var metadata = await client.GetAsync(PrivateMcpHttpAdapter.MetadataPath, deadline.Token);
                using var mcp = await client.PostAsync("mcp", null, deadline.Token);
                Require(metadata.StatusCode == HttpStatusCode.NotFound && mcp.StatusCode == HttpStatusCode.NotFound &&
                    !mcp.Headers.Contains("WWW-Authenticate"));
                await app.StopAsync(deadline.Token);
            });
            foreach (Configuration wrong in new[] { equivalent, opposite })
                await Check(path + " mismatched Map is rejected before routes and leaves valid binding available", async () =>
                {
                    WebApplicationBuilder builder = Builder();
                    selected.Add(builder.Services);
                    await using WebApplication app = builder.Build();
                    Deny(() => wrong.Map(app));
                    Require(((IEndpointRouteBuilder)app).DataSources.Count == 0);
                    selected.Map(app);
                    Require(((IEndpointRouteBuilder)app).DataSources.Count > 0);
                });
            await Check(path + " duplicate Map is rejected before changing endpoint sources", async () =>
            {
                WebApplicationBuilder builder = Builder();
                selected.Add(builder.Services);
                await using WebApplication app = builder.Build();
                selected.Map(app);
                var before = ((IEndpointRouteBuilder)app).DataSources.ToArray();
                Deny(() => selected.Map(app));
                Require(before.SequenceEqual(((IEndpointRouteBuilder)app).DataSources));
            });
            foreach (string mutation in new[] { "duplicate", "override", "replace", "mixed" })
                await Check(path + " late DI " + mutation + " cannot redirect tool authorization", async () =>
                {
                    WebApplicationBuilder builder = Builder();
                    selected.Add(builder.Services);
                    if (mutation == "replace") selected.RemoveBoundary(builder.Services);
                    (mutation == "mixed" ? opposite : mutation == "duplicate" ? selected : equivalent).AddBoundary(builder.Services);
                    await using WebApplication app = builder.Build();
                    Deny(() => selected.Map(app));
                    Require(((IEndpointRouteBuilder)app).DataSources.Count == 0);
                });
            await Check(path + " separate hosts retain independent mapping guards", async () =>
            {
                WebApplicationBuilder first = Builder(), second = Builder();
                selected.Add(first.Services);
                selected.Add(second.Services);
                await using WebApplication firstApp = first.Build();
                await using WebApplication secondApp = second.Build();
                selected.Map(firstApp);
                selected.Map(secondApp);
                Require(((IEndpointRouteBuilder)firstApp).DataSources.Count > 0 &&
                    ((IEndpointRouteBuilder)secondApp).DataSources.Count > 0);
            });
            Require(selected.SourceCalls == 0 && equivalent.SourceCalls == 0 && opposite.SourceCalls == 0);
        }
        Console.WriteLine($"PASS {checks} HTTP composition checks; actual DI/TestServer, no keys or Work accessed.");
        return checks;
    }

    private static WebApplicationBuilder Builder()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        return builder;
    }

    private static void Deny(Action action)
    {
        try { action(); throw new Exception("Expected composition rejection."); }
        catch (InvalidOperationException error) { Require(error.Message == Failure && error.InnerException is null); }
    }

    private static void Require(bool value)
    {
        if (!value) throw new Exception("HTTP composition assertion failed.");
    }

    private sealed class Configuration
    {
        private readonly bool _rotating;
        private readonly ConnectorReadBoundary _fixed;
        private readonly RotatingConnectorReadBoundary _rotation;
        private readonly ForbiddenSource _source = new();
        public int SourceCalls => _source.Calls;

        public Configuration(RSA rsa, bool rotating)
        {
            _rotating = rotating;
            _fixed = new ConnectorReadBoundary(Issuer, Resource, new RsaSecurityKey(rsa.ExportParameters(false)), _source);
            var configuration = new OAuthDiscoverySummary(Issuer, new Uri(Issuer + "/authorize"),
                new Uri(Issuer + "/token"), new Uri(Issuer + "/certs"));
            _rotation = new RotatingConnectorReadBoundary(new CustomerSigningKeyCache(configuration, _source, TimeProvider.System), Resource, _source);
        }

        public void Add(IServiceCollection services)
        {
            if (_rotating) PrivateMcpHttpAdapter.AddServices(services, _rotation);
            else PrivateMcpHttpAdapter.AddServices(services, _fixed);
        }

        public void Map(WebApplication app)
        {
            if (_rotating) PrivateMcpHttpAdapter.Map(app, _rotation);
            else PrivateMcpHttpAdapter.Map(app, _fixed);
        }

        public void AddBoundary(IServiceCollection services)
        {
            if (_rotating) services.AddSingleton(_rotation);
            else services.AddSingleton(_fixed);
        }

        public void RemoveBoundary(IServiceCollection services)
        {
            if (_rotating) services.RemoveAll<RotatingConnectorReadBoundary>();
            else services.RemoveAll<ConnectorReadBoundary>();
        }
    }

    private sealed class ForbiddenSource : ICustomerJwksDocumentSource, ICustomerWorkStatusSource
    {
        public int Calls { get; private set; }

        public Task<string> ReadAsync(Uri pinnedAddress, CancellationToken token)
        {
            Calls++;
            throw new Exception("Composition must not access keys.");
        }

        public Task<WorkStatusResult> ReadAuthorizedAsync(ConnectorIdentity identity, long workId, CancellationToken token)
        {
            Calls++;
            throw new Exception("Composition must not access Work.");
        }
    }
}
