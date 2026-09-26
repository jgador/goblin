using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

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

    // Executed only in an inspection pod with the Work PVC mounted read-only.
    // No workspace files, archives, or tool output are persisted by this reader.
    public static object Read(string root, string? requested)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (requested is not null)
        {
            if (requested.Length > 1024 || Path.IsPathRooted(requested) || requested.Split('/').Any(x => x is ".." or "." or ""))
                throw new IOException("Invalid workspace path.");
            string path = root;
            foreach (string part in requested.Split('/'))
            {
                path = Path.Combine(path, part);
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Workspace links cannot be opened.");
            }
            if (Hidden(requested) || !File.Exists(path)) throw new IOException("Workspace file is unavailable.");
            RequireRegularFile(path);
            using FileStream file = File.OpenRead(path);
            byte[] buffer = new byte[1024 * 1024 + 1];
            int size = 0, read;
            while ((read = file.Read(buffer, size, buffer.Length - size)) > 0)
            {
                size += read;
                if (size == buffer.Length) throw new IOException("Workspace file is too large to preview.");
            }
            return new { path = requested, text = Encoding.UTF8.GetString(buffer, 0, size) };
        }
        var files = new List<object>();
        bool truncated = false;
        int visited = 0;
        void Add(string directory)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).OrderBy(x => Path.GetFileName(x) == "repository" ? 0 : 1).ThenBy(x => x, StringComparer.Ordinal))
            {
                if (++visited > 100000 || files.Count >= 1000) { truncated = true; return; }
                string relative = Path.GetRelativePath(root, path);
                if (relative.Length > 1024 || Hidden(relative)) continue;
                FileAttributes attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (attributes.HasFlag(FileAttributes.Directory)) Add(path);
                else files.Add(new { path = relative, size = new FileInfo(path).Length });
                if (truncated) return;
            }
        }
        Add(root);
        return new { files = files.ToArray(), truncated };
    }
    private static void RequireRegularFile(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        var start = new ProcessStartInfo("/usr/bin/stat") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment["LC_ALL"] = "C";
        start.ArgumentList.Add("--format=%F"); start.ArgumentList.Add("--"); start.ArgumentList.Add(path);
        using Process process = Process.Start(start)!;
        string kind = process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit();
        // Named pipes, sockets, and devices are not previews. Opening a FIFO
        // could otherwise block the inspection process indefinitely.
        if (process.ExitCode != 0 || !kind.StartsWith("regular", StringComparison.Ordinal)) throw new IOException("Workspace entry is not a regular file.");
    }
    private static bool Hidden(string path) => path == "repository/.git" || path.StartsWith("repository/.git/", StringComparison.Ordinal) ||
        (path == ".goblin" || path.StartsWith(".goblin/", StringComparison.Ordinal)) && path != ".goblin/changes.patch";

    public static int Run(string? path)
    {
        try { Console.WriteLine(JsonSerializer.Serialize(Read("/workspace", path))); return 0; }
        catch { Console.Error.WriteLine("Workspace files are unavailable or exceed preview limits."); return 1; }
    }
}
