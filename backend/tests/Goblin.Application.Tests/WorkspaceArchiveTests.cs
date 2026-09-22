using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Goblin.Application.Workspaces;
using Goblin.Execution;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class WorkspaceArchiveTests
{
    [Fact]
    public void ArchivePreservesIgnoredFilesAndOutputsInAFreshDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        string directory = Path.Combine(Path.GetTempPath(), "goblin-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "source/repository"));
        try
        {
            File.WriteAllText(Path.Combine(directory, "source/repository/.gitignore"), "report.txt\n");
            File.WriteAllText(Path.Combine(directory, "source/repository/report.txt"), "Important ignored output");
            File.WriteAllText(Path.Combine(directory, "source/changes.patch"), "Saved diff");
            WorkspaceFiles.Pack(Path.Combine(directory, "source"), Path.Combine(directory, "archive.tar.gz"));
            WorkspaceFiles.Unpack(Path.Combine(directory, "archive.tar.gz"), Path.Combine(directory, "restored"));
            Assert.Equal("Important ignored output", File.ReadAllText(Path.Combine(directory, "restored/repository/report.txt")));
            Assert.Equal("Saved diff", File.ReadAllText(Path.Combine(directory, "restored/changes.patch")));
            object preview = WorkspaceArchive.Inspect(File.ReadAllBytes(Path.Combine(directory, "archive.tar.gz")), "repository/report.txt");
            Assert.Contains("Important ignored output", System.Text.Json.JsonSerializer.Serialize(preview));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData("../outside")]
    [InlineData("/tmp/outside")]
    public void UntrustedArchiveCannotWriteOutsideWorkspace(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        string directory = Path.Combine(Path.GetTempPath(), "goblin-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string archive = Path.Combine(directory, "archive.tar.gz");
            using (var gzip = new GZipStream(File.Create(archive), CompressionMode.Compress))
            using (var tar = new TarWriter(gzip))
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream([1, 2, 3]) });
            Assert.Throws<IOException>(() => WorkspaceFiles.Unpack(archive, Path.Combine(directory, "destination")));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void InspectionMountsRepositoryReadOnlyAndCredentialsOnlyInRestoreContainer()
    {
        using var api = new KubernetesApi("http://localhost");
        var host = new InspectionHost(api, new("agents", "image", "unused", "http://broker"));
        JsonObject manifest = host.Manifest(new(Guid.NewGuid(), 1, 1, Guid.NewGuid(), "run-1-1"), false);
        JsonNode spec = manifest["spec"]!["podTemplate"]!["spec"]!;
        Assert.True(spec["containers"]![0]!["volumeMounts"]![0]!["readOnly"]!.GetValue<bool>());
        Assert.DoesNotContain("restore", spec["containers"]![0]!["volumeMounts"]!.ToJsonString());
        Assert.False(spec["automountServiceAccountToken"]!.GetValue<bool>());
        Assert.False(spec["initContainers"]![0]!["volumeMounts"]![0]!["readOnly"]!.GetValue<bool>());
    }
}
