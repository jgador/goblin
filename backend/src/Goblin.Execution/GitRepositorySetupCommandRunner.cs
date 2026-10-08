using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Goblin.Execution;

internal sealed class GitRepositorySetupCommandRunner
{
    private readonly string _checkout;
    private readonly Dictionary<string, string> _environment;

    internal GitRepositorySetupCommandRunner(string checkout, Dictionary<string, string> environment)
    {
        _checkout = checkout;
        _environment = environment;
    }

    internal async Task<string> RunAsync(string command, string[] arguments, int outputLimit, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        token = deadline.Token;
        var start = new ProcessStartInfo(command)
        { WorkingDirectory = _checkout, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear();
        foreach (KeyValuePair<string, string> entry in _environment) start.Environment[entry.Key] = entry.Value;
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new IOException("Setup verification unavailable.");
        try
        {
            Task<string> output = ReadAsync(process.StandardOutput, outputLimit, token);
            Task<string> error = ReadAsync(process.StandardError, 16384, token);
            // A failed reader must retire the process before awaiting the other
            // pipe; otherwise an oversized output could deadlock verification.
            var pending = new List<Task> { output, error, process.WaitForExitAsync(token) };
            while (pending.Count > 0)
            {
                Task completed = await Task.WhenAny(pending);
                await completed; pending.Remove(completed);
            }
            if (process.ExitCode != 0) throw new IOException("Repository setup verification failed.");
            return await output;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    private static async Task<string> ReadAsync(StreamReader reader, int maximum, CancellationToken token)
    {
        var text = new StringBuilder();
        char[] buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (text.Length + count > maximum) throw new IOException("Setup verification output is too large.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
