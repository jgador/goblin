using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.GitRepositories;

namespace Goblin.Execution;

// All filesystem inspection and verification happens inside the isolated worker.
// The controller stores observations, but never executes a remembered command.
public sealed class GitRepositorySetupWorkspace
{
    private readonly string _checkout;
    private readonly GitRepositorySetupCommandRunner _commands;

    public GitRepositorySetupWorkspace(string checkout, Dictionary<string, string> environment)
    {
        _checkout = Path.GetFullPath(checkout);
        _commands = new(_checkout, environment);
    }

    public static string EnvironmentIdentity(string image) => image + " | " +
        System.Runtime.InteropServices.RuntimeInformation.OSDescription + " | " +
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture + " | " +
        typeof(GitRepositorySetupWorkspace).Assembly.ManifestModule.ModuleVersionId;

    public async Task<GitRepositorySetupMemory[]> SelectAsync(GitRepositorySetupMemory[] memories, string environment, CancellationToken token)
    {
        if (!memories.Any(x => x.Environment == environment)) return [];
        string configuration = await ConfigurationHashAsync(token);
        var selected = new List<GitRepositorySetupMemory>();
        int bytes = 0;
        foreach (GitRepositorySetupMemory memory in memories.OrderByDescending(x => x.VerifiedAt))
        {
            VerifiedGitRepositorySetup observation = memory.Observation;
            if (memory.Environment != environment || !GitRepositorySetupRules.Valid(observation) ||
                observation.ConfigurationHash != configuration || selected.Any(x => x.Observation.Setup.Topic == observation.Setup.Topic)) continue;
            SetupFile[] current = await FilesAsync(observation.Setup.Files, token);
            if (!current.SequenceEqual(observation.Files.OrderBy(x => x.Path, StringComparer.Ordinal))) continue;
            int size = JsonSerializer.SerializeToUtf8Bytes(memory, ExecutionFiles.Json).Length;
            if (bytes + size > 24000) continue;
            selected.Add(memory); bytes += size;
            if (selected.Count == GitRepositorySetupRules.MaxObservations) break;
        }
        return [.. selected];
    }

    public async Task<VerifiedGitRepositorySetup[]> VerifyAsync(GitRepositorySetup[] setups, CancellationToken token)
    {
        if (setups.Length > GitRepositorySetupRules.MaxObservations || !setups.All(GitRepositorySetupRules.Valid) ||
            setups.Select(x => x.Topic).Distinct(StringComparer.Ordinal).Count() != setups.Length ||
            JsonSerializer.SerializeToUtf8Bytes(setups, ExecutionFiles.Json).Length > 48000)
            throw new IOException("Invalid repository setup observation.");
        if (setups.Length == 0) return [];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        string configuration = await ConfigurationHashAsync(deadline.Token);
        var observations = new List<VerifiedGitRepositorySetup>();
        foreach (GitRepositorySetup setup in setups)
        {
            SetupFile[] files = await FilesAsync(setup.Files, deadline.Token);
            if (!files.Any(x => x.Sha256 is not null)) throw new IOException("Setup has no repository evidence.");
            foreach (SetupCheck check in setup.Checks)
            {
                string output = await _commands.RunAsync("/bin/sh", ["-c", check.Command], 16384, deadline.Token);
                if (output.Trim() != check.ExpectedOutput.Trim()) throw new IOException("Repository setup verification failed.");
            }
            observations.Add(new(setup, files, configuration));
        }
        // Checks must inspect prepared state, not change the requirements that
        // justified it. Never associate a successful check with different inputs.
        if (await ConfigurationHashAsync(deadline.Token) != configuration) throw new IOException("Setup inputs changed during verification.");
        foreach (VerifiedGitRepositorySetup observation in observations)
            if (!(await FilesAsync(observation.Setup.Files, deadline.Token)).SequenceEqual(observation.Files))
                throw new IOException("Setup inputs changed during verification.");
        return [.. observations];
    }

    private async Task<SetupFile[]> FilesAsync(string[] paths, CancellationToken token)
    {
        var files = new List<SetupFile>();
        foreach (string path in paths.Order(StringComparer.Ordinal)) files.Add(new(path, await HashFileAsync(path, token)));
        return [.. files];
    }

    private async Task<string?> HashFileAsync(string relative, CancellationToken token)
    {
        if (!GitRepositorySetupRules.SafePath(relative)) throw new IOException("Invalid setup input path.");
        string path = _checkout;
        foreach (string part in relative.Split('/'))
        {
            path = Path.Combine(path, part);
            if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
                throw new IOException("Setup inputs cannot follow symbolic links.");
        }
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("Setup input is too large.");
        await using FileStream file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token));
    }

    private async Task<string> ConfigurationHashAsync(CancellationToken token)
    {
        string listing = await _commands.RunAsync("git", ["-c", "core.hooksPath=/dev/null", "ls-files", "--cached", "--others", "--exclude-standard", "-z"], 4 * 1024 * 1024, token);
        string[] paths = [.. listing.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(IsConfiguration)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (paths.Length > 2048) throw new IOException("Too many setup inputs.");
        SetupFile[] files = await FilesAsync(paths, token);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(files.Where(x => x.Sha256 is not null))));
    }

    private static bool IsConfiguration(string path)
    {
        string name = Path.GetFileName(path);
        return name is "AGENTS.md" or "global.json" or "NuGet.Config" or "nuget.config" or "packages.lock.json" or
            "Directory.Build.props" or "Directory.Build.targets" or "Directory.Packages.props" or
            "pyproject.toml" or "uv.lock" or "poetry.lock" or "Pipfile" or "Pipfile.lock" or "setup.py" or "setup.cfg" or ".python-version" or
            "package.json" or "package-lock.json" or "yarn.lock" or "pnpm-lock.yaml" or ".nvmrc" or ".node-version" or
            "Cargo.toml" or "Cargo.lock" or "go.mod" or "go.sum" or "Gemfile" or "Gemfile.lock" or
            "Dockerfile" or "devcontainer.json" or ".tool-versions" or "mise.toml" ||
            name.EndsWith("proj", StringComparison.Ordinal) ||
            (name.StartsWith("requirements", StringComparison.Ordinal) && name.EndsWith(".txt", StringComparison.Ordinal));
    }

}
