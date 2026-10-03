using System;
using System.Threading;
using System.Threading.Tasks;

namespace Goblin.Contracts.Runtime;

// Product-facing model options. Protocol objects and account credentials stay
// inside the runtime integration.
public sealed class RuntimeModel
{
    public RuntimeModel(string id, string model, string displayName, string defaultReasoningEffort,
        string[] supportedReasoningEfforts, bool isDefault, bool isNew = false)
    {
        Id = id;
        Model = model;
        DisplayName = displayName;
        DefaultReasoningEffort = defaultReasoningEffort;
        SupportedReasoningEfforts = supportedReasoningEfforts;
        IsDefault = isDefault;
        IsNew = isNew;
    }

    public string Id { get; init; }

    public string Model { get; init; }

    public string DisplayName { get; init; }

    public string DefaultReasoningEffort { get; init; }

    public string[] SupportedReasoningEfforts { get; init; }

    public bool IsDefault { get; init; }

    public bool IsNew { get; init; }
}

public interface IModelCatalogSource
{
    string Runtime { get; }

    string ExecutableStamp();

    Task<RuntimeModel[]> ListAsync(CancellationToken token);
}

public sealed class ModelCatalogView
{
    public ModelCatalogView(RuntimeModel[] models, bool hasMore, string? defaultModel,
        DateTimeOffset? fetchedAt, bool stale, bool refreshing, bool unavailable)
    {
        Models = models;
        HasMore = hasMore;
        DefaultModel = defaultModel;
        FetchedAt = fetchedAt;
        Stale = stale;
        Refreshing = refreshing;
        Unavailable = unavailable;
    }

    public RuntimeModel[] Models { get; init; }

    public bool HasMore { get; init; }

    public string? DefaultModel { get; init; }

    public DateTimeOffset? FetchedAt { get; init; }

    public bool Stale { get; init; }

    public bool Refreshing { get; init; }

    public bool Unavailable { get; init; }
}
