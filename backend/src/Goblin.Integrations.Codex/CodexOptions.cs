using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Goblin.Integrations.Codex;

public sealed record CodexOptions
{
    public required string CodexHome { get; init; }
    public required string Home { get; init; }
    public required string Workspace { get; init; }
    public string Command { get; init; } = FindCommand();
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public IReadOnlyDictionary<string, string?> Environment { get; init; } =
        System.Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(x => (string)x.Key, x => (string?)x.Value);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public bool RepositoryExecution { get; init; }

    // File metadata is a cheap local revision check for the installed CLI.
    // A running app-server keeps its startup revision until it is restarted.
    public string ExecutableStamp()
    {
        string? path = File.Exists(Command) ? Path.GetFullPath(Command) :
            (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(directory => Path.Combine(directory, Command + (OperatingSystem.IsWindows() ? ".exe" : "")))
                .FirstOrDefault(File.Exists);
        if (path is null) return Command;
        var file = new FileInfo(path);
        return $"{file.FullName}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
    }

    // npm distributes the official Rust binary. Prefer the pinned local package,
    // including on Windows where a .cmd shim cannot be spawned without a shell.
    public static string FindCommand()
    {
        string arch = RuntimeInformation.ProcessArchitecture switch { Architecture.Arm64 => "arm64", Architecture.X64 => "x64", _ => "" };
        string platform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
        string targetArch = arch == "arm64" ? "aarch64" : "x86_64";
        string target = platform switch
        {
            "win32" => $"{targetArch}-pc-windows-msvc",
            "darwin" => $"{targetArch}-apple-darwin",
            _ => $"{targetArch}-unknown-linux-musl"
        };
        string path = Path.GetFullPath(Path.Combine("node_modules", "@openai", $"codex-{platform}-{arch}", "vendor", target, "bin",
            OperatingSystem.IsWindows() ? "codex.exe" : "codex"));
        return File.Exists(path) ? path : "codex";
    }

    internal ProcessStartInfo CreateStartInfo()
    {
        var info = new ProcessStartInfo(Command)
        {
            WorkingDirectory = Workspace,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = new System.Text.UTF8Encoding(false, true)
        };
        info.Environment.Clear();
        string[] allowed = ["PATH", "LANG", "LC_ALL", "TZ", "SSL_CERT_FILE", "SSL_CERT_DIR",
            "NODE_EXTRA_CA_CERTS", "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY",
            "SystemRoot", "WINDIR", "PATHEXT"];
        foreach (string name in allowed)
            if (Environment.TryGetValue(name, out string? value) && value is not null) info.Environment[name] = value;
        info.Environment["HOME"] = Home;
        info.Environment["USERPROFILE"] = Home;
        info.Environment["CODEX_HOME"] = CodexHome;
        foreach (string argument in Arguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("app-server");
        void Config(string value) { info.ArgumentList.Add("-c"); info.ArgumentList.Add(value); }
        Config("model_provider=\"openai\"");
        Config("cli_auth_credentials_store=\"file\"");
        Config("analytics.enabled=false");
        // Model-directed commands need the code-mode host even when the optional
        // code_mode feature is off. Only isolated repository workers may run them.
        foreach (string feature in new[] { "shell_tool", "unified_exec", "code_mode_host" })
            Config($"features.{feature}={(RepositoryExecution ? "true" : "false")}");
        string[] disabled = ["shell_snapshot", "view_image", "image_generation",
            "apps", "plugins", "remote_plugin", "multi_agent", "hooks", "memories", "goals",
            "code_mode", "skill_search", "skill_mcp_dependency_install",
            "sleep_tool", "request_permissions_tool", "workspace_dependencies"];
        foreach (string feature in disabled) Config($"features.{feature}=false");
        Config("web_search=\"disabled\"");
        Config(RepositoryExecution ? "sandbox_mode=\"danger-full-access\"" : "sandbox_mode=\"read-only\"");
        Config("approval_policy=\"never\"");
        Config("project_doc_max_bytes=0");
        Config("skills.include_instructions=false");
        Config("memories.generate_memories=false");
        Config("memories.use_memories=false");
        return info;
    }
}
