using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Protocol;

namespace Goblin.Integrations.Codex;

internal static class PluginSetupSkill
{
    internal const string Name = "goblin-plugin-setup";
    internal static string Markdown
    {
        get
        {
            using Stream stream = typeof(PluginSetupSkill).Assembly.GetManifestResourceStream("Goblin.PluginSetup.Skill")!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }

    internal static async Task<string> LoadAsync(CodexClient codex, CancellationToken token)
    {
        string path = await InstallAsync(codex.Options, token);
        SkillsListResponse skills = await codex.RequestAsync<SkillsListParams, SkillsListResponse>("skills/list", new()
        {
            Cwds = [codex.Options.Workspace],
            ForceReload = true
        }, token);
        if (!skills.Data.SelectMany(entry => entry.Skills).Any(skill => skill.Name == Name && skill.Path == path && skill.Enabled))
            throw new IntegrationFailure("plugin_skill_unavailable", "Codex could not load Goblin's bundled preparation skill. Check the installed runtime version.");
        return path;
    }

    private static async Task<string> InstallAsync(CodexOptions options, CancellationToken token)
    {
        string directory = Path.Combine(options.CodexHome, "skills", Name);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string path = Path.Combine(directory, "SKILL.md");
        await File.WriteAllTextAsync(path, Markdown, new UTF8Encoding(false), token);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }
}
