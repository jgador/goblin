using System;
using System.Threading;
using System.Threading.Tasks;

namespace Goblin.Contracts;

// Read-only preparation is separate from both account changes and MCP authorization.
public interface ICodexPluginSetup
{
    Task<PluginCreatorAccess> CheckPluginCreatorAsync(CancellationToken token);
    Task<ChatGptPluginDraft> PreparePluginAsync(ChatGptPluginContext context, CancellationToken token);
}

public sealed class ChatGptPluginContext
{
    public ChatGptPluginContext(string serverUrl, string issuer, string clientId, string scope, string? registeredAppId)
    {
        ServerUrl = serverUrl;
        Issuer = issuer;
        ClientId = clientId;
        Scope = scope;
        RegisteredAppId = registeredAppId;
    }

    public string ServerUrl { get; }
    public string Issuer { get; }
    public string ClientId { get; }
    public string Scope { get; }
    public string? RegisteredAppId { get; }
}

public sealed class PluginCreatorAccess
{
    public PluginCreatorAccess(bool runtimeSupported, bool skillAvailable, string? skillSha256,
        bool appInstalled, bool appEnabled, bool appCallable, PluginCreatorTool[] tools, DateTimeOffset checkedAt)
    {
        RuntimeSupported = runtimeSupported;
        SkillAvailable = skillAvailable;
        SkillSha256 = skillSha256;
        AppInstalled = appInstalled;
        AppEnabled = appEnabled;
        AppCallable = appCallable;
        Tools = tools;
        CheckedAt = checkedAt;
    }

    public bool RuntimeSupported { get; }
    public bool SkillAvailable { get; }
    public string? SkillSha256 { get; }
    public bool AppInstalled { get; }
    public bool AppEnabled { get; }
    public bool AppCallable { get; }
    public PluginCreatorTool[] Tools { get; }
    public DateTimeOffset CheckedAt { get; }
}

public sealed class PluginCreatorTool
{
    public PluginCreatorTool(string name, bool enabled, bool readOnly)
    {
        Name = name;
        Enabled = enabled;
        ReadOnly = readOnly;
    }

    public string Name { get; }
    public bool Enabled { get; }
    public bool ReadOnly { get; }
}

public sealed class ChatGptPluginDraft
{
    public ChatGptPluginDraft(string description, string creatorPrompt, string skillMarkdown, string model,
        long durationMs, PluginCreatorAccess creatorAccess)
    {
        Description = description;
        CreatorPrompt = creatorPrompt;
        SkillMarkdown = skillMarkdown;
        Model = model;
        DurationMs = durationMs;
        CreatorAccess = creatorAccess;
    }

    public string Description { get; }
    public string CreatorPrompt { get; }
    public string SkillMarkdown { get; }
    public string Model { get; }
    public long DurationMs { get; }
    public PluginCreatorAccess CreatorAccess { get; }
}
