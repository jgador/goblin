using System;
using Goblin.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// EF tools resolve Name=ConnectionStrings:Goblin from this developer-only host.
// The selected PostgreSQL profile supplies its connection through the environment.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Logging.ClearProviders();
string? connection = builder.Configuration.GetConnectionString("Goblin");
if (!string.IsNullOrWhiteSpace(connection)) builder.Services.AddGoblinPersistence(connection);
using IHost host = builder.Build();
