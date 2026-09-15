using System;
using System.IO;
using Goblin.Database;
using Goblin.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// EF tools resolve Name=ConnectionStrings:Goblin from this host. Building it
// neither opens a database connection nor starts the web server or Codex.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Logging.ClearProviders();
string? connection = builder.Configuration.GetConnectionString("Goblin");
if (!string.IsNullOrWhiteSpace(connection)) builder.Services.AddGoblinPersistence(connection);
using IHost host = builder.Build();

if (args.Length != 2 || args[0] != "apply")
{
    Console.Error.WriteLine("Usage: dotnet run --project backend/tools/Goblin.Database -- apply backend/database/migrations");
    return 1;
}

string? adminConnection = builder.Configuration.GetConnectionString("GoblinAdmin");
if (string.IsNullOrWhiteSpace(adminConnection))
{
    Console.Error.WriteLine("Set ConnectionStrings:GoblinAdmin in backend/tools/Goblin.Database/appsettings.json to the schema administrator's connection string.");
    return 1;
}

try
{
    await SqlMigrations.ApplyAsync(adminConnection, Path.GetFullPath(args[1]), Console.Out);
    return 0;
}
catch (Exception error)
{
    // Connection strings and SQL errors may contain credentials or row data.
    Console.Error.WriteLine($"Database update failed ({error.GetType().Name}). Check connectivity, administrator access, and migration files.");
    return 1;
}
