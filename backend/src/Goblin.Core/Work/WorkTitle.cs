using System;
using System.Text;

namespace Goblin.Core.Work;

public static class WorkTitle
{
    public const int MaxLength = 90;

    public static string? Normalize(string? value)
    {
        if (value is null) return null;
        foreach (char character in value)
            if (char.IsControl(character) || character is '\u2028' or '\u2029') return null;
        string title = value.Trim();
        if (title.Length == 0) return null;
        int length = 0;
        foreach (Rune _ in title.EnumerateRunes())
            if (++length > MaxLength) return null;
        return title;
    }

    internal static string Require(string? value) =>
        Normalize(value) ?? throw new WorkRuleException(WorkRule.InvalidTitle);
}
