using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts.Conversations;
using Microsoft.Extensions.DependencyInjection;

namespace Goblin.Web;

internal sealed class ExternalConversationAdapter : IExternalConversations
{
    private readonly IServiceScopeFactory _scopes;

    public ExternalConversationAdapter(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task AcceptAsync(ExternalMessage message, CancellationToken token)
    {
        using IServiceScope scope = _scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ExternalConversationStore>().AcceptAsync(message, token);
    }

    public async Task<ExternalReply?> ProcessNextAsync(ExternalInstallation installation, CancellationToken token)
    {
        using IServiceScope scope = _scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ExternalConversationStore>().ProcessNextAsync(installation, token);
    }
}
