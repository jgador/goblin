using System;
using System.Text.RegularExpressions;

namespace Goblin.Integrations.ChatGpt;

public static partial class ChatGptCallback
{
    public static void Require(string value)
    {
        if (!Callback().IsMatch(value))
            throw new ArgumentException("Copy the exact ChatGPT OAuth callback from connection settings.");
    }

    [GeneratedRegex("\\Ahttps://chatgpt\\.com/(connector_platform_oauth_redirect|connector/oauth/[A-Za-z0-9_-]+)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Callback();
}
