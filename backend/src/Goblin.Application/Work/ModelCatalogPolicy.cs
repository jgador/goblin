using System;
using System.Linq;
using System.Text.Json;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;

namespace Goblin.Application.Work;

internal static class ModelCatalogPolicy
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(1);

    internal static bool SameAccount(long? savedGeneration, long currentGeneration) =>
        savedGeneration == currentGeneration;

    internal static bool Due(DateTime? fetchedAt, bool refreshFailed, string? savedStamp,
        string currentStamp, DateTime utcNow) =>
        fetchedAt is null || refreshFailed || fetchedAt.Value.Add(Lifetime) <= utcNow || savedStamp != currentStamp;

    internal static bool CanAttempt(DateTime? retryAfter, DateTime utcNow) =>
        retryAfter is null || retryAfter <= utcNow;

    internal static bool Fresh(long? savedGeneration, long currentGeneration, DateTime? fetchedAt,
        bool refreshFailed, string? savedStamp, string currentStamp, DateTime utcNow) =>
        SameAccount(savedGeneration, currentGeneration) &&
        !Due(fetchedAt, refreshFailed, savedStamp, currentStamp, utcNow);

    internal static DateTime RetryAfter(DateTime utcNow) => utcNow.Add(FailureCooldown);

    internal static RuntimeModel[] Read(string? json) => json is null
        ? []
        : JsonSerializer.Deserialize<RuntimeModel[]>(json, ContractJson.Options) ?? [];

    internal static RuntimeModel[] MarkNew(RuntimeModel[] discovered, RuntimeModel[] previous)
    {
        var previousNames = previous.Select(x => x.Model).ToHashSet(StringComparer.Ordinal);
        return [.. discovered.Select(x => new RuntimeModel(
            x.Id, x.Model, x.DisplayName, x.DefaultReasoningEffort, x.SupportedReasoningEfforts,
            x.IsDefault, previous.Length > 0 && !previousNames.Contains(x.Model)))];
    }

    internal static RuntimeModel[] Order(RuntimeModel[] models, string? selected) =>
        [.. models.OrderByDescending(x => x.Model == selected)
            .ThenByDescending(x => x.IsDefault)
            .ThenByDescending(x => x.IsNew)];

    internal static void ValidateSelection(RuntimeModel[] models, string? model, string? effort)
    {
        RuntimeModel? choice = (model is null
            ? models.FirstOrDefault(x => x.IsDefault)
            : models.FirstOrDefault(x => x.Model == model)) ?? throw new ApplicationFailure("model_unavailable");
        if (effort is not null && !choice.SupportedReasoningEfforts.Contains(effort, StringComparer.Ordinal))
            throw new ApplicationFailure("reasoning_effort_unavailable");
    }
}
