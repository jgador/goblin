using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Goblin.Core.Work;

namespace Goblin.Application.Work;

// Resolve explicit references in user-supplied Work context. A match only asks
// for setup; repository enablement and authorization are checked separately.
public static class GitRepositoryReferences
{
    public static string[] Find(WorkSnapshot work, string[] knownGitRepositories, string? answer = null)
    {
        string text = string.Join("\n", GitRepositoryIntent.Inputs(work).Append(answer ?? ""));
        return FindText(text, knownGitRepositories);
    }

    public static string[] FindText(string text, string[] knownGitRepositories)
    {
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text,
            @"(?<![\w./@:-])(?:https?://)?github\.com/([a-z0-9_.-]+/[a-z0-9_.-]+)", RegexOptions.IgnoreCase))
        {
            string name = match.Groups[1].Value.TrimEnd('.');
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (name.Split('/').All(x => x is not ("." or "..") && x.Length > 0)) matches.Add(name);
        }
        // URLs from other hosts and paths inside URLs must not become repository names.
        string names = Regex.Replace(GitRepositoryIntent.WithoutBranchNames(text), @"(?:https?://|github\.com/)[^\s<>]+", " ", RegexOptions.IgnoreCase);
        foreach (Match match in Regex.Matches(names, @"(?<![\w./@:-])([a-z0-9_.-]+/[a-z0-9_.-]+)(?![\w./-])", RegexOptions.IgnoreCase))
        {
            string name = match.Groups[1].Value.TrimEnd('.');
            if (name.Split('/').All(x => x is not ("." or "..") && x.Length > 0)) matches.Add(name);
        }
        foreach (string name in knownGitRepositories)
            if (Regex.IsMatch(names, @"(?<![\w./:-])" + Regex.Escape(name) + @"(?![\w./-])", RegexOptions.IgnoreCase))
                matches.Add(name);
        foreach (IGrouping<string, string> group in knownGitRepositories.GroupBy(x => x.Split('/')[1], StringComparer.OrdinalIgnoreCase))
            if (Regex.IsMatch(names, @"(?<![\w./:-])" + Regex.Escape(group.Key) + @"(?![\w./-])", RegexOptions.IgnoreCase))
                foreach (string name in group) matches.Add(name);
        return [.. matches.Order(StringComparer.OrdinalIgnoreCase)];
    }
}
