using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts;
using Goblin.Protocol;

namespace Goblin.Integrations.Codex;

internal sealed class PluginCreatorProbe
{
    private readonly CodexClient _codex;

    internal PluginCreatorProbe(CodexClient codex) => _codex = codex;

    // Public directory metadata, not a guessed tool name or creation API.
    internal const string AppId = "connector_openai_plugin_creator";
    internal const string PluginId = "plugin_connector_1p_e1a10c53223481918a42f1510ec46c1e";
    internal const string Marketplace = "oai-maintained-plugins";

    internal async Task<PluginCreatorAccess> CheckAsync(CancellationToken token)
    {
        string? threadId = null;
        try
        {
            // This thread only reads metadata. Never start a model turn with this
            // configuration: the preparation turn keeps all app tools disabled.
            ThreadStartResponse thread = await _codex.RequestAsync<ThreadStartParams, ThreadStartResponse>("thread/start", new()
            {
                Cwd = _codex.Options.Workspace,
                Ephemeral = true,
                ApprovalPolicy = AskForApproval.Never,
                Sandbox = SandboxMode.ReadOnly,
                Config = new Dictionary<string, JsonElement>
                {
                    ["features.apps"] = JsonSerializer.SerializeToElement(true),
                    ["apps"] = JsonSerializer.SerializeToElement(new Dictionary<string, object>
                    {
                        ["_default"] = new { enabled = false },
                        [AppId] = new
                        {
                            enabled = true,
                            destructive_enabled = false,
                            open_world_enabled = false,
                            default_tools_approval_mode = "prompt"
                        }
                    })
                }
            }, token);
            threadId = thread.Thread.Id;
            if (!thread.Thread.Ephemeral || thread.ModelProvider != "openai" || thread.Sandbox is not ReadOnlySandboxPolicy ||
                thread.ApprovalPolicy is not StringAskForApproval { Value: AskForApprovalValue.Never })
                throw IntegrationFailure.RuntimeUnavailable();
            AppsInstalledResponse installed = await _codex.RequestAsync<AppsInstalledParams, AppsInstalledResponse>("app/installed",
                new() { ThreadId = threadId, ForceRefresh = true }, token);
            AppsReadResponse metadata = await _codex.RequestAsync<AppsReadParams, AppsReadResponse>("app/read",
                new() { ThreadId = threadId, AppIds = [AppId], IncludeTools = true }, token);
            bool skillSupported = true;
            string? contents = null;
            try
            {
                PluginSkillReadResponse skill = await _codex.RequestAsync<PluginSkillReadParams, PluginSkillReadResponse>("plugin/skill/read",
                    new() { RemoteMarketplaceName = Marketplace, RemotePluginId = PluginId, SkillName = "plugin-creator" }, token);
                contents = skill.Contents;
            }
            catch (IntegrationFailure error) when (error.Code == "codex_request_failed") { skillSupported = false; }
            InstalledApp? app = installed.Apps.SingleOrDefault(a => a.Id == AppId);
            ConnectorMetadata? details = metadata.Apps.SingleOrDefault(a => a.Id == AppId);
            bool skillAvailable = !string.IsNullOrWhiteSpace(contents) && contents.Length <= 131072;
            return new(skillSupported, skillAvailable,
                skillAvailable ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents!))).ToLowerInvariant() : null,
                app is not null, app?.Enabled == true, app?.Callable == true,
                [.. (details?.ToolSummaries ?? []).Where(t => t.Name.Length <= 200 && !t.Name.Any(char.IsControl))
                    .Take(100).Select(t => new PluginCreatorTool(t.Name, t.IsEnabled ?? true, t.IsReadOnly ?? false))],
                DateTimeOffset.UtcNow);
        }
        catch (IntegrationFailure error) when (error.Code == "codex_request_failed")
        {
            return new(false, false, null, false, false, false, [], DateTimeOffset.UtcNow);
        }
        finally
        {
            if (threadId is not null && _codex.Ready)
                try { await _codex.RequestAsync<ThreadUnsubscribeParams, ThreadUnsubscribeResponse>("thread/unsubscribe", new() { ThreadId = threadId }); }
                catch { }
        }
    }
}
