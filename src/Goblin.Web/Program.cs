using System;
using Goblin.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

var portValue = Environment.GetEnvironmentVariable("GOBLIN_PORT") ?? "8787";
if (!int.TryParse(portValue, out var port) || port is < 1 or > 65535)
    throw new ArgumentException("GOBLIN_PORT must be a valid port.");
var origin = Environment.GetEnvironmentVariable("GOBLIN_PUBLIC_ORIGIN") ?? $"http://localhost:{port}";
var host = Environment.GetEnvironmentVariable("GOBLIN_HOST") ?? "127.0.0.1";
await using WebApplication app = await GoblinApplication.CreateAsync(new()
{
    DataDirectory = Environment.GetEnvironmentVariable("GOBLIN_DATA_DIR") ?? ".goblin-auth",
    PublicOrigin = origin,
    ListenUrl = $"http://{host}:{port}",
    ConfigureCodex = options => options with { Command = Environment.GetEnvironmentVariable("GOBLIN_CODEX_COMMAND") ?? options.Command }
});
Console.WriteLine($"Goblin authentication preview: {origin}");
Console.WriteLine($"Workspace access code file: {app.Services.GetRequiredService<Workspace>().TokenFile}");
await app.RunAsync();
