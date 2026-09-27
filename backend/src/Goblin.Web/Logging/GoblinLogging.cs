using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Goblin.Web;

public static class GoblinLogging
{
    public static ILoggingBuilder AddGoblinJsonConsole(this ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddJsonConsole(options =>
        {
            options.TimestampFormat = "O";
            options.UseUtcTimestamp = true;
            options.IncludeScopes = true;
        });
        // Bound the console queue without blocking application work on log delivery.
        logging.Services.Configure<ConsoleLoggerOptions>(options =>
        {
            options.MaxQueueLength = 1024;
            options.QueueFullMode = ConsoleLoggerQueueFullMode.DropWrite;
        });
        return logging;
    }
}
