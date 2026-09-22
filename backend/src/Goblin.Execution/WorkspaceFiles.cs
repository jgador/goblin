using System;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Goblin.Execution;

public static class WorkspaceFiles
{
    public static void EnsureQuiescent()
    {
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
                if (process.Id != Environment.ProcessId && !process.HasExited)
                    throw new IOException("Workspace still has running processes.");
        }
    }
    public static void Pack(string root, string destination)
    {
        using var gzip = new GZipStream(File.Create(destination), CompressionLevel.Fastest);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax);
        void Add(string directory)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order())
            {
                string relative = Path.GetRelativePath(root, path);
                if (relative == ".goblin") continue;
                FileAttributes attributes = File.GetAttributes(path);
                writer.WriteEntry(path, relative);
                if (attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint)) Add(path);
            }
        }
        Add(root);
        string diff = Path.Combine(root, ".goblin", "changes.patch");
        if (File.Exists(diff)) writer.WriteEntry(diff, ".goblin/changes.patch");
    }
    public static void Unpack(string source, string root)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        root = Path.GetFullPath(root).TrimEnd('/') + "/";
        Directory.CreateDirectory(root);
        using var gzip = new GZipStream(File.OpenRead(source), CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        TarEntry? entry; int entries = 0; long length = 0;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (++entries > 100000 || (length += entry.Length) > 4L * 1024 * 1024 * 1024) throw new IOException("Workspace archive exceeds limits.");
            string destination = Path.GetFullPath(Path.Combine(root, entry.Name));
            if (!destination.StartsWith(root, StringComparison.Ordinal) || entry.Name.Split('/').Contains("..")) throw new IOException("Invalid workspace path.");
            string? parent = Path.GetDirectoryName(destination);
            // A prior archive entry must never redirect later writes through a link.
            for (string? part = parent; part is not null && part.StartsWith(root, StringComparison.Ordinal); part = Path.GetDirectoryName(part))
                if (new DirectoryInfo(part).LinkTarget is not null) throw new IOException("Invalid workspace link.");
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                if (new FileInfo(destination).LinkTarget is not null) throw new IOException("Invalid workspace link.");
            }
            Directory.CreateDirectory(parent!);
            if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(destination);
            else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
            {
                using var file = new FileStream(destination, FileMode.Create, FileAccess.Write);
                entry.DataStream?.CopyTo(file);
                File.SetUnixFileMode(destination, entry.Mode & (UnixFileMode)511);
            }
            else if (entry.EntryType == TarEntryType.SymbolicLink)
            {
                string target = Path.GetFullPath(Path.Combine(parent!, entry.LinkName));
                if (Path.IsPathRooted(entry.LinkName) || !target.StartsWith(root, StringComparison.Ordinal)) throw new IOException("External workspace link cannot be restored.");
                File.CreateSymbolicLink(destination, entry.LinkName);
            }
            else throw new IOException("Unsupported workspace archive entry.");
        }
    }
}
