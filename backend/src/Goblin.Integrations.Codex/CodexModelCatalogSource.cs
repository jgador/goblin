using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Protocol;

namespace Goblin.Integrations.Codex;

public sealed class CodexModelCatalogSource : IModelCatalogSource
{
    private readonly CodexClient _codex;

    public CodexModelCatalogSource(CodexClient codex) => _codex = codex;

    public string Runtime => "codex";

    public string ExecutableStamp() => _codex.Options.ExecutableStamp();

    public async Task<RuntimeModel[]> ListAsync(CancellationToken token)
    {
        await _codex.StartAsync(token);
        if (_codex.StartedExecutableStamp != ExecutableStamp())
            throw IntegrationFailure.RuntimeUnavailable();
        var result = new List<RuntimeModel>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (int page = 0; page < 20; page++)
        {
            ModelListResponse response = await _codex.RequestAsync<ModelListParams, ModelListResponse>(
                "model/list", new() { IncludeHidden = false, Cursor = cursor, Limit = 100 }, token);
            foreach (Model model in response.Data)
            {
                if (model.Hidden || string.IsNullOrWhiteSpace(model.Id) ||
                    string.IsNullOrWhiteSpace(model.ModelValue) || string.IsNullOrWhiteSpace(model.DisplayName)) continue;
                result.Add(new(model.Id, model.ModelValue, model.DisplayName,
                    model.DefaultReasoningEffort,
                    [.. model.SupportedReasoningEfforts.Select(x => x.ReasoningEffort).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal)],
                    model.IsDefault));
            }
            cursor = response.NextCursor;
            if (string.IsNullOrEmpty(cursor))
            {
                RuntimeModel[] visible = [.. result.DistinctBy(x => x.Model, StringComparer.Ordinal)];
                if (visible.Length == 0) throw IntegrationFailure.RuntimeUnavailable();
                return visible;
            }
            if (!cursors.Add(cursor) || result.Count > 1000) break;
        }
        throw IntegrationFailure.RuntimeUnavailable();
    }
}
