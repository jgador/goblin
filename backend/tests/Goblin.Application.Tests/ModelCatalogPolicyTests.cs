using System;
using System.Linq;
using System.Text.Json;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class ModelCatalogPolicyTests
{
    [Fact]
    public void FreshnessAndRetryPolicyCoverAccountExecutableAgeAndFailure()
    {
        DateTime now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        DateTime recent = now.AddHours(-1);

        Assert.True(ModelCatalogPolicy.SameAccount(4, 4));
        Assert.False(ModelCatalogPolicy.SameAccount(null, 4));
        Assert.False(ModelCatalogPolicy.Due(recent, false, "stamp", "stamp", now));
        Assert.True(ModelCatalogPolicy.Due(now.AddHours(-24), false, "stamp", "stamp", now));
        Assert.True(ModelCatalogPolicy.Due(recent, true, "stamp", "stamp", now));
        Assert.True(ModelCatalogPolicy.Due(recent, false, "old", "stamp", now));
        Assert.True(ModelCatalogPolicy.Fresh(4, 4, recent, false, "stamp", "stamp", now));
        Assert.False(ModelCatalogPolicy.Fresh(3, 4, recent, false, "stamp", "stamp", now));
        Assert.True(ModelCatalogPolicy.CanAttempt(null, now));
        Assert.True(ModelCatalogPolicy.CanAttempt(now, now));
        Assert.False(ModelCatalogPolicy.CanAttempt(now.AddSeconds(1), now));
        Assert.Equal(now.AddMinutes(1), ModelCatalogPolicy.RetryAfter(now));
    }

    [Fact]
    public void DiscoveryPreservesKnownModelsAndOrdersSelectionDefaultAndNewModels()
    {
        RuntimeModel[] previous = [Model("a", isDefault: false)];
        RuntimeModel[] discovered = [Model("a", false), Model("b", true), Model("c", false)];

        RuntimeModel[] marked = ModelCatalogPolicy.MarkNew(discovered, previous);
        Assert.False(marked[0].IsNew);
        Assert.True(marked[1].IsNew);
        Assert.True(marked[2].IsNew);
        Assert.Equal(new[] { "b", "c", "a" }, ModelCatalogPolicy.Order(marked, null).Select(x => x.Model));
        Assert.Equal(new[] { "a", "b", "c" }, ModelCatalogPolicy.Order(marked, "a").Select(x => x.Model));

        string json = JsonSerializer.Serialize(marked, ContractJson.Options);
        Assert.Equivalent(marked, ModelCatalogPolicy.Read(json), strict: true);
        Assert.Empty(ModelCatalogPolicy.Read(null));
    }

    [Fact]
    public void SelectionUsesTheDefaultAndRequiresAnAdvertisedEffort()
    {
        RuntimeModel[] models = [Model("standard", true, "medium", "high"), Model("small", false, "low")];
        ModelCatalogPolicy.ValidateSelection(models, null, "high");
        ModelCatalogPolicy.ValidateSelection(models, "small", "low");
        AssertFailure("model_unavailable", () => ModelCatalogPolicy.ValidateSelection(models, "missing", null));
        AssertFailure("reasoning_effort_unavailable", () => ModelCatalogPolicy.ValidateSelection(models, "small", "high"));
        AssertFailure("model_unavailable", () => ModelCatalogPolicy.ValidateSelection([], null, null));
    }

    private static RuntimeModel Model(string name, bool isDefault, params string[] efforts) =>
        new(name, name, name, efforts.FirstOrDefault() ?? "medium", efforts.Length == 0 ? ["medium"] : efforts, isDefault);

    private static void AssertFailure(string code, Action action)
    {
        ApplicationFailure failure = Assert.Throws<ApplicationFailure>(action);
        Assert.Equal(code, failure.Code);
    }
}
