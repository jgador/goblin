using System;
using System.IO;
using Goblin.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Load administrator configuration for the separately invoked SQL migration runner.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Logging.ClearProviders();
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
