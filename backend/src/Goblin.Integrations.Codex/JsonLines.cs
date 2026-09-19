using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace Goblin.Integrations.Codex;

internal static class JsonLines
{
    // Bound each frame while reading, including peers that never send a newline.
    internal const int MaximumLength = 2 * 1024 * 1024;

    public static async IAsyncEnumerable<string> ReadAsync(TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new char[8192];
        var line = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == '\n')
                {
                    if (line.Length > 0)
                    {
                        var value = line.ToString();
                        line.Clear();
                        if (!string.IsNullOrWhiteSpace(value)) yield return value;
                    }
                }
                else
                {
                    if (line.Length >= MaximumLength) throw IntegrationFailure.RuntimeUnavailable();
                    line.Append(buffer[i]);
                }
            }
        }
        // A partial last frame is a failed transport, never a successful response.
        if (line.Length > 0) throw IntegrationFailure.RuntimeUnavailable();
    }
}
