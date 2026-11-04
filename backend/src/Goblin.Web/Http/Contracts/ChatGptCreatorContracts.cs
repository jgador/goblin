using System;
using System.Text.Json.Serialization;
using Goblin.Contracts;

namespace Goblin.Web;

internal sealed class ChatGptCreatorRequest
{
    [JsonPropertyName("registeredAppId")]
    public string? RegisteredAppId { get; init; }
}

internal sealed class ChatGptCreatorAccessView
{
    private readonly PluginCreatorAccess _access;

    public ChatGptCreatorAccessView(PluginCreatorAccess access)
    {
        _access = access;
        Tools = Array.ConvertAll(access.Tools, tool => new ChatGptCreatorToolView(tool));
    }

    [JsonPropertyName("runtimeSupported")]
    public bool RuntimeSupported => _access.RuntimeSupported;

    [JsonPropertyName("skillAvailable")]
    public bool SkillAvailable => _access.SkillAvailable;

    [JsonPropertyName("skillSha256")]
    public string? SkillSha256 => _access.SkillSha256;

    [JsonPropertyName("appInstalled")]
    public bool AppInstalled => _access.AppInstalled;

    [JsonPropertyName("appEnabled")]
    public bool AppEnabled => _access.AppEnabled;

    [JsonPropertyName("appCallable")]
    public bool AppCallable => _access.AppCallable;

    [JsonPropertyName("tools")]
    public ChatGptCreatorToolView[] Tools { get; }

    [JsonPropertyName("checkedAt")]
    public DateTimeOffset CheckedAt => _access.CheckedAt;

    // Metadata access does not establish a supported external-MCP registration contract.
    [JsonPropertyName("automaticCreationAvailable")]
    public bool AutomaticCreationAvailable => false;
}

internal sealed class ChatGptCreatorToolView
{
    private readonly PluginCreatorTool _tool;

    public ChatGptCreatorToolView(PluginCreatorTool tool) => _tool = tool;

    [JsonPropertyName("name")]
    public string Name => _tool.Name;

    [JsonPropertyName("enabled")]
    public bool Enabled => _tool.Enabled;

    [JsonPropertyName("readOnly")]
    public bool ReadOnly => _tool.ReadOnly;
}

internal sealed class ChatGptCreatorDraftView
{
    private readonly ChatGptPluginDraft _draft;

    public ChatGptCreatorDraftView(ChatGptPluginDraft draft)
    {
        _draft = draft;
        CreatorAccess = new(draft.CreatorAccess);
    }

    [JsonPropertyName("description")]
    public string Description => _draft.Description;

    [JsonPropertyName("creatorPrompt")]
    public string CreatorPrompt => _draft.CreatorPrompt;

    [JsonPropertyName("model")]
    public string Model => _draft.Model;

    [JsonPropertyName("durationMs")]
    public long DurationMs => _draft.DurationMs;

    [JsonPropertyName("creatorAccess")]
    public ChatGptCreatorAccessView CreatorAccess { get; }

    [JsonPropertyName("pluginRegistered")]
    public bool PluginRegistered => false;

    [JsonPropertyName("connectionVerified")]
    public bool ConnectionVerified => false;
}
