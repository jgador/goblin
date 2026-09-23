using System;
using System.Linq;
using System.Text.RegularExpressions;
using Goblin.Core.Repositories;

namespace Goblin.Contracts.Runtime;

// Validation at the worker/controller boundary; no Work lifecycle rules live here.
public static class RepositorySetupRules
{
    public const int MaxObservations = 8;
    public const int MaxPayloadBytes = 65536;

    public static bool SafeText(string? value, int maximum, bool empty = false) =>
        value is not null && value.Length <= maximum && (empty || !string.IsNullOrWhiteSpace(value)) && !value.Contains('\0') &&
        !Regex.IsMatch(value, @"(?i)(-----BEGIN .*PRIVATE KEY|\b(?:gh[pousr]_|github_pat_|sk-)[a-z0-9_-]{12,}|https?://[^\s/]+@|(?:password|api[_-]?key|access[_-]?token|secret)\s*[=:]\s*[^\s$]{4,}|/run/credentials|auth\.json)", RegexOptions.CultureInvariant);

    public static bool SafePath(string? value) => SafeText(value, 512) &&
        !value!.StartsWith('/') && !value.Contains('\\') && !value.Contains(':') &&
        value.Split('/').All(x => x.Length > 0 && x is not ("." or ".." or ".git" or ".env"));

    public static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static bool Valid(RepositorySetup? setup) => setup is not null &&
        SafeText(setup.Topic, 80) && SafeText(setup.Reason, 1000) &&
        setup.Tools is { Length: > 0 and <= 16 } && setup.Tools.All(x => SafeText(x, 160)) &&
        setup.Commands is { Length: <= 16 } && setup.Commands.All(x => SafeText(x, 2000)) &&
        setup.Files is { Length: > 0 and <= 32 } && setup.Files.All(SafePath) &&
        setup.Files.Distinct(StringComparer.Ordinal).Count() == setup.Files.Length &&
        setup.Checks is { Length: > 0 and <= 8 } && setup.Checks.All(x => x is not null &&
            SafeText(x.Command, 2000) && SafeText(x.ExpectedOutput, 2000, empty: true));

    public static bool Valid(VerifiedRepositorySetup? observation) => observation is not null && Valid(observation.Setup) &&
        Hash(observation.ConfigurationHash) && observation.Files is not null &&
        observation.Files.Length == observation.Setup.Files.Length &&
        observation.Files.All(x => x is not null && SafePath(x.Path) && (x.Sha256 is null || Hash(x.Sha256))) &&
        observation.Files.Select(x => x.Path).Order(StringComparer.Ordinal)
            .SequenceEqual(observation.Setup.Files.Order(StringComparer.Ordinal));
}
