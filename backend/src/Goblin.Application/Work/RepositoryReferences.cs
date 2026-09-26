using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Goblin.Core.Work;

namespace Goblin.Application.Work;

// Resolve explicit references in user-supplied Work context. A match only asks
// for setup; repository enablement and authorization are checked separately.
public static class RepositoryReferences
{
    public static string[] Find(WorkSnapshot work, string[] enabledRepositories, string? answer = null)
    {
        string text = string.Join("\n", new[] { work.Objective, answer }
            .Concat(work.Messages.Select(x => x.Text))
            .Concat(work.Decisions.Select(x => x.Answer))
            .Concat(work.Results.Select(x => x.RequestedChanges)));
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text,
            @"(?<![\w./@:-])(?:https?://)?github\.com/([a-z0-9_.-]+/[a-z0-9_.-]+)", RegexOptions.IgnoreCase))
        {
            string name = match.Groups[1].Value.TrimEnd('.');
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (name.Split('/').All(x => x is not ("." or "..") && x.Length > 0)) matches.Add(name);
        }
        foreach (string name in enabledRepositories)
            if (Regex.IsMatch(text, @"(?<![\w./:-])" + Regex.Escape(name) + @"(?![\w./-])", RegexOptions.IgnoreCase))
                matches.Add(name);
        return [.. matches.Order(StringComparer.OrdinalIgnoreCase)];
    }
}
