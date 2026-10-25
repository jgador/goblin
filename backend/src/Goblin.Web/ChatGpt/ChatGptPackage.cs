using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Goblin.Integrations.ChatGpt;

namespace Goblin.Web;

internal static partial class ChatGptPackage
{
    internal static ChatGptPackageView Create(ChatGptConfiguration configuration, ChatGptPackageRequest request)
    {
        string name = Text(request.Name, "Listing name", 30);
        string publisher = Text(request.Publisher, "Publisher name", 80);
        string subtitle = Text(request.Subtitle, "Short description", 30);
        string description = Text(request.Description, "Description", 4000, multiline: true);
        string packageId = Text(request.PackageId, "Plugin ID", 64);
        if (!PackageId().IsMatch(packageId)) throw Invalid("Use lowercase words separated by hyphens for the plugin ID.");
        string statusWork = WorkId(request.StatusWorkId);
        string resultWork = WorkId(request.ResultWorkId);
        string privateWork = WorkId(request.PrivateWorkId);
        if (new[] { statusWork, resultWork, privateWork }.Distinct().Count() != 3)
            throw Invalid("Choose three distinct sample Work IDs: status-only, shared result, and unshared Work.");
        var presentation = new
        {
            displayName = name,
            shortDescription = subtitle,
            longDescription = description,
            developerName = publisher,
            category = "Developer Tools",
            capabilities = new[] { "Read shared Work", "Read approved results" },
            websiteURL = Address(request.WebsiteUrl, "Website URL"),
            supportURL = Address(request.SupportUrl, "Support URL"),
            privacyPolicyURL = Address(request.PrivacyUrl, "Privacy policy URL"),
            termsOfServiceURL = Address(request.TermsUrl, "Terms of service URL"),
            defaultPrompt = new[] { "List the Work shared with this connection." },
            logo = "./assets/icon.svg",
            composerIcon = "./assets/icon.svg"
        };
        object[] positive = [
            Case("List shared Work", "List the Work shared with this connection.", "goblin_list_work", "Return only the demo user's unexpired shared Work."),
            Case("Read shared status", $"Show the status of Work {statusWork}.", "goblin_get_work_status", "Return the shared Work summary matching Goblin."),
            Case("Read attention", $"What needs attention on Work {statusWork}?", "goblin_get_work_attention", "Return the shared Work's attention codes matching Goblin."),
            Case("Read approved result", $"Show the approved result of Work {resultWork}.", "goblin_get_work_result", "Return only the approved attempt pinned by the demo user's grant."),
            Case("Follow up on shared Work", $"Show Work {statusWork}. What is its status now?", "goblin_get_work_status", "Reuse the shared Work ID and return its current status.")
        ];
        object[] negative = [
            Case("Deny unshared Work", $"Show the status of Work {privateWork}.", "goblin_get_work_status", "Deny access without returning private Work fields."),
            Case("Deny unshared result", $"Show the approved result of Work {statusWork}.", "goblin_get_work_result", "Deny access because this grant shares status only."),
            Case("Decline unsupported writes", "Start new Work in Goblin and retry failed Work.", "", "Explain that these actions must be performed in Goblin; do not execute a write.")
        ];
        var plugin = new Dictionary<string, object>
        {
            ["$schema"] = "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json",
            ["name"] = packageId,
            ["version"] = "1.0.0",
            ["description"] = description,
            ["author"] = new { name = publisher },
            ["extensions"] = new Dictionary<string, object>
            {
                ["com.openai"] = new
                {
                    @interface = presentation,
                    review = new { test_cases = new { positive, negative }, demo_recording_url = Address(request.VideoUrl, "Demo video URL") },
                    publication = new { release_notes = "Initial release: read explicitly shared Work and approved results." }
                }
            }
        };
        var mcp = new Dictionary<string, object>
        {
            ["$schema"] = "https://agent-plugins.org/schemas/1.0.0/mcp.schema.json",
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["goblin"] = new
                {
                    type = "streamable-http",
                    url = configuration.Resource,
                    extensions = new Dictionary<string, object>
                    {
                        ["com.openai"] = new
                        {
                            auth = new
                            {
                                type = "oauth",
                                client = new { mode = "provided", clientId = ChatGptConfiguration.ClientId, tokenEndpointAuthMethod = "client_secret_basic" },
                                baseScopes = ChatGptConfiguration.AuthorizationScopes.Split(' ')
                            }
                        }
                    }
                }
            }
        };
        var options = new JsonSerializerOptions { WriteIndented = true };
        string pluginJson = JsonSerializer.Serialize(plugin, options);
        string mcpJson = JsonSerializer.Serialize(mcp, options);
        using Stream logo = typeof(ChatGptPackage).Assembly.GetManifestResourceStream("Goblin.ChatGpt.Logo")!;
        XDocument svg = XDocument.Load(logo);
        // Center the existing artwork in the square canvas required by the directory.
        double[] box = [.. svg.Root!.Attribute("viewBox")!.Value.Split(' ').Select(v => double.Parse(v, CultureInfo.InvariantCulture))];
        double size = Math.Max(box[2], box[3]);
        svg.Root.SetAttributeValue("viewBox", FormattableString.Invariant($"{box[0] - (size - box[2]) / 2} {box[1] - (size - box[3]) / 2} {size} {size}"));
        svg.Root.SetAttributeValue("width", size.ToString(CultureInfo.InvariantCulture));
        svg.Root.SetAttributeValue("height", size.ToString(CultureInfo.InvariantCulture));
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "plugin.json", pluginJson);
            Write(archive, "mcp.json", mcpJson);
            Write(archive, "assets/icon.svg", svg.ToString());
        }
        return new ChatGptPackageView { PluginJson = pluginJson, McpJson = mcpJson, ArchiveBase64 = Convert.ToBase64String(bytes.ToArray()) };
    }

    private static object Case(string description, string prompt, string tools, string expected) =>
        new { description, prompt, tools_triggered = tools, expected_behavior = expected };

    private static void Write(ZipArchive archive, string path, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open());
        writer.Write(text);
    }

    private static string Text(string? value, string label, int limit, bool multiline = false)
    {
        string text = value?.Trim() ?? "";
        if (text.Length == 0 || text.Length > limit || text.Any(c => char.IsControl(c) && !(multiline && c == '\n')))
            throw Invalid($"Enter {label.ToLowerInvariant()} using 1–{limit} characters.");
        return text;
    }

    private static string Address(string? value, string label)
    {
        string text = Text(value, label, 1024);
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? address) || address.Scheme != "https" || address.UserInfo.Length > 0)
            throw Invalid($"Enter an HTTPS {label.ToLowerInvariant()} without embedded credentials.");
        return address.AbsoluteUri;
    }

    private static string WorkId(string? value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0)
            throw Invalid("Enter the saved decimal Work IDs from your demo account in Review tests.");
        return id.ToString(CultureInfo.InvariantCulture);
    }

    private static PublicError Invalid(string message) => new("invalid_plugin_package", message);

    [GeneratedRegex("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageId();
}

internal sealed class ChatGptPackageRequest
{
    [JsonPropertyName("packageId")]
    public string? PackageId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("publisher")]
    public string? Publisher { get; init; }

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("websiteUrl")]
    public string? WebsiteUrl { get; init; }

    [JsonPropertyName("supportUrl")]
    public string? SupportUrl { get; init; }

    [JsonPropertyName("privacyUrl")]
    public string? PrivacyUrl { get; init; }

    [JsonPropertyName("termsUrl")]
    public string? TermsUrl { get; init; }

    [JsonPropertyName("videoUrl")]
    public string? VideoUrl { get; init; }

    [JsonPropertyName("statusWorkId")]
    public string? StatusWorkId { get; init; }

    [JsonPropertyName("resultWorkId")]
    public string? ResultWorkId { get; init; }

    [JsonPropertyName("privateWorkId")]
    public string? PrivateWorkId { get; init; }
}

internal sealed class ChatGptPackageView
{
    [JsonPropertyName("pluginJson")]
    public required string PluginJson { get; init; }

    [JsonPropertyName("mcpJson")]
    public required string McpJson { get; init; }

    [JsonPropertyName("archiveBase64")]
    public required string ArchiveBase64 { get; init; }
}
