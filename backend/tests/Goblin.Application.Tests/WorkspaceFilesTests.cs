using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Goblin.Execution;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class WorkspaceFilesTests
{
    [Fact]
    public void InspectionReadsIgnoredFilesWithoutCopyingOrArchivingThem()
    {
        string root = Path.Combine(Path.GetTempPath(), "goblin-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "repository"));
        try
        {
            File.WriteAllText(Path.Combine(root, "repository/.gitignore"), "report.txt\n");
            File.WriteAllText(Path.Combine(root, "repository/report.txt"), "Important ignored output");
            Directory.CreateDirectory(Path.Combine(root, ".goblin"));
            File.WriteAllText(Path.Combine(root, ".goblin/changes.patch"), "saved diff");
            Assert.Contains("Important ignored output", JsonSerializer.Serialize(WorkspaceFiles.Read(root, "repository/report.txt")));
            Assert.Contains("repository/report.txt", JsonSerializer.Serialize(WorkspaceFiles.Read(root, null)));
            Assert.Contains("saved diff", JsonSerializer.Serialize(WorkspaceFiles.Read(root, ".goblin/changes.patch")));
            Assert.Empty(Directory.GetFiles(root, "*.gz", SearchOption.AllDirectories));
            using FileStream large = File.Create(Path.Combine(root, "repository/large")); large.SetLength(2 * 1024 * 1024); large.Dispose();
            Assert.Throws<IOException>(() => WorkspaceFiles.Read(root, "repository/large"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("../outside")]
    [InlineData("/etc/passwd")]
    [InlineData("repository/../../outside")]
    public void InspectionRejectsPathsOutsideWorkspace(string path) => Assert.Throws<IOException>(() => WorkspaceFiles.Read(Path.GetTempPath(), path));

    [Fact]
    public void InspectionRejectsExternalLinks()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "goblin-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "external"), "/etc");
            Assert.Throws<IOException>(() => WorkspaceFiles.Read(root, "external/passwd"));
            Assert.DoesNotContain("external", JsonSerializer.Serialize(WorkspaceFiles.Read(root, null)));
            var start = new ProcessStartInfo("mkfifo"); start.ArgumentList.Add(Path.Combine(root, "pipe"));
            using Process fifo = Process.Start(start)!; fifo.WaitForExit(); Assert.Equal(0, fifo.ExitCode);
            Assert.Throws<IOException>(() => WorkspaceFiles.Read(root, "pipe"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void InspectionMountsWorkPvcReadOnlyWithoutCredentialsOrRestoreContainer()
    {
        using var api = new KubernetesApi("http://localhost");
        var host = new InspectionHost(api, new("agents", "image", "unused", "http://broker"));
        JsonObject manifest = JsonSerializer.SerializeToNode(host.Manifest(new(9007199254740993, 1, 1, "k8s/agents/work-1"), false), Goblin.Execution.Kubernetes.KubernetesJson.Options)!.AsObject();
        JsonNode spec = manifest["spec"]!["podTemplate"]!["spec"]!;
        Assert.True(spec["containers"]![0]!["volumeMounts"]![0]!["readOnly"]!.GetValue<bool>());
        Assert.Equal("work-1", spec["volumes"]![0]!["persistentVolumeClaim"]!["claimName"]!.GetValue<string>());
        Assert.False(spec["automountServiceAccountToken"]!.GetValue<bool>());
        Assert.Null(spec["initContainers"]);
        Assert.DoesNotContain("secret", spec.ToJsonString());
    }
}
