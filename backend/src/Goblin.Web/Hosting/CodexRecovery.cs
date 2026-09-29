using System;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Integrations.Codex;
using Microsoft.Extensions.Hosting;

namespace Goblin.Web;

internal sealed class CodexRecovery : BackgroundService
{
    private readonly CodexClient _codex;

    public CodexRecovery(CodexClient codex) => _codex = codex;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        bool firstAttempt = true;
        do
        {
            try { await _codex.StartAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch
            {
                if (firstAttempt) Console.Error.WriteLine("Codex could not start. Install the pinned Codex CLI, then restart Goblin.");
            }
            firstAttempt = false;
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _codex.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }
}
