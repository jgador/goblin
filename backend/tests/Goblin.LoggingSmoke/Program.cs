using System;
using System.Collections.Generic;
using Goblin.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

// Disposable verification workload, excluded from Goblin's production image.
// The same formatter and category configuration as the web host are exercised.
IConfiguration configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json").Build();
using ILoggerFactory factory = LoggerFactory.Create(logging =>
    logging.AddConfiguration(configuration.GetSection("Logging")).AddGoblinJsonConsole());
ILogger logger = factory.CreateLogger("Goblin.LoggingSmoke");
string marker = args.Length > 0 ? args[0] : "goblin-logging-smoke";
using (logger.BeginScope(new Dictionary<string, object>
{
    ["TraceId"] = "00112233445566778899aabbccddeeff",
    ["WorkId"] = "9007199254740993"
}))
{
    logger.LogInformation(new EventId(42), "Logging smoke {Marker} {AttemptId}", marker, 9007199254740993L);
    logger.LogWarning(new InvalidOperationException("Synthetic smoke failure"), "Multiline {Marker}\nsecond line", marker);
}
ILogger frameworkLogger = factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");
frameworkLogger.LogInformation("Framework logging smoke {Marker}", marker);
frameworkLogger.LogDebug("filtered-smoke-debug-details");
Console.WriteLine("Plain console " + marker);
Console.Error.WriteLine("Plain stderr " + marker);
