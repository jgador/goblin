using System;
using System.Text.Json.Serialization;

namespace Goblin.Web.Http.Contracts;

public sealed class RuntimeModel
{
    [JsonConstructor]
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

    [JsonPropertyName("id")]
    public string Id { get; init; }

    [JsonPropertyName("model")]
    public string Model { get; init; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; }

    [JsonPropertyName("defaultReasoningEffort")]
    public string DefaultReasoningEffort { get; init; }

    [JsonPropertyName("supportedReasoningEfforts")]
    public string[] SupportedReasoningEfforts { get; init; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; init; }

    [JsonPropertyName("isNew")]
    public bool IsNew { get; init; }

    public static RuntimeModel From(Goblin.Contracts.Runtime.RuntimeModel value) =>
        new(value.Id, value.Model, value.DisplayName, value.DefaultReasoningEffort, value.SupportedReasoningEfforts,
            value.IsDefault, value.IsNew);
}

public sealed class ModelCatalogView
{
    [JsonConstructor]
    public ModelCatalogView(RuntimeModel[] models, bool hasMore, string? defaultModel, DateTimeOffset? fetchedAt,
        bool stale, bool refreshing, bool unavailable)
    {
        Models = models;
        HasMore = hasMore;
        DefaultModel = defaultModel;
        FetchedAt = fetchedAt;
        Stale = stale;
        Refreshing = refreshing;
        Unavailable = unavailable;
    }

    [JsonPropertyName("models")]
    public RuntimeModel[] Models { get; init; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }

    [JsonPropertyName("defaultModel")]
    public string? DefaultModel { get; init; }

    [JsonPropertyName("fetchedAt")]
    public DateTimeOffset? FetchedAt { get; init; }

    [JsonPropertyName("stale")]
    public bool Stale { get; init; }

    [JsonPropertyName("refreshing")]
    public bool Refreshing { get; init; }

    [JsonPropertyName("unavailable")]
    public bool Unavailable { get; init; }

    public static ModelCatalogView From(Goblin.Contracts.Runtime.ModelCatalogView value) =>
        new(Array.ConvertAll(value.Models, RuntimeModel.From), value.HasMore, value.DefaultModel, value.FetchedAt,
            value.Stale, value.Refreshing, value.Unavailable);
}
