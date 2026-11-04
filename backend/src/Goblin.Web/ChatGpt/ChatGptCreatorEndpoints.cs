using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Goblin.Application.Connections;
using Goblin.Contracts;
using Goblin.Integrations.ChatGpt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Goblin.Web;

internal static partial class ChatGptCreatorEndpoints
{
    internal static void Map(WebApplication app, string root, ChatGptConfiguration configuration)
    {
        app.MapPost(root + "/creator/check", async (HttpContext context, ConnectionService connection) =>
            Results.Json(new ChatGptCreatorAccessView(await connection.CheckPluginCreatorAsync(context.RequestAborted))));
        app.MapPost(root + "/creator/prepare", async (HttpContext context, ConnectionService connection) =>
        {
            ChatGptCreatorRequest request = ApiRequest.Body<ChatGptCreatorRequest>(context, ContractJson.Options);
            string? appId = request.RegisteredAppId?.Trim();
            if (appId?.Length == 0) appId = null;
            if (appId is not null && !RegisteredAppId().IsMatch(appId))
                throw new PublicError("invalid_plugin_connection", "Use the plugin_asdk_app_ technical ID of your existing ChatGPT MCP connection, or leave it empty.");
            var metadata = new ChatGptPluginContext(configuration.Resource, configuration.Issuer,
                ChatGptConfiguration.ClientId, ChatGptConfiguration.AuthorizationScopes, appId);
            ChatGptPluginDraft draft = await connection.PreparePluginAsync(metadata, context.RequestAborted);
            return Results.Json(new ChatGptCreatorDraftView(draft, Archive(draft, metadata)));
        });
    }

    private static string Archive(ChatGptPluginDraft draft, ChatGptPluginContext context)
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write("skills/goblin-plugin-setup/SKILL.md", draft.SkillMarkdown);
            Write("creator-prompt.txt", draft.CreatorPrompt);
            Write("connection.json", JsonSerializer.Serialize(new
            {
                serverUrl = context.ServerUrl,
                issuer = context.Issuer,
                clientId = context.ClientId,
                scope = context.Scope,
                registeredAppId = context.RegisteredAppId,
                pluginRegistered = false,
                connectionVerified = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            void Write(string path, string content)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(content);
            }
        }
        return Convert.ToBase64String(bytes.ToArray());
    }

    [GeneratedRegex("^plugin_asdk_app_[a-z0-9]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex RegisteredAppId();
}
