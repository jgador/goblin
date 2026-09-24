using System;
using System.Threading;
using System.Threading.Tasks;

namespace Goblin.Contracts.Runtime;

// Product-facing model options. Protocol objects and account credentials stay
// inside the runtime integration.
public sealed record RuntimeModel(string Id, string Model, string DisplayName,
    string DefaultReasoningEffort, string[] SupportedReasoningEfforts,
    bool IsDefault, bool IsNew = false);

public interface IModelCatalogSource
{
    string Runtime { get; }
    string ExecutableStamp();
    Task<RuntimeModel[]> ListAsync(CancellationToken token);
}

public sealed record ModelCatalogView(RuntimeModel[] Models, bool HasMore,
    string? DefaultModel, DateTimeOffset? FetchedAt, bool Stale,
    bool Refreshing, bool Unavailable);
