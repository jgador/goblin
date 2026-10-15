using Goblin.Mcp.Prototype;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Offline review must exit before reading session configuration or creating a host.
if (args.Length != 0)
    return await OfflineOAuthMetadataCommand.RunAsync(args, Console.Out, Console.Error, CancellationToken.None);

PrototypeOptions options;
try { options = PrototypeOptions.Read(Environment.GetEnvironmentVariable); }
catch (ArgumentException)
{
    Console.Error.WriteLine("MCP configuration invalid. Configure a fixed origin, local session and explicit Work read grants.");
    return 1;
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
// stdout belongs exclusively to the MCP transport. Do not log HTTP headers or bodies.
builder.Logging.ClearProviders();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
{
    Timeout = TimeSpan.FromSeconds(5),
    MaxResponseContentBufferSize = 128 * 1024
});
builder.Services.AddSingleton<WorkStatusReader>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<WorkTools>();
await builder.Build().RunAsync();
return 0;
