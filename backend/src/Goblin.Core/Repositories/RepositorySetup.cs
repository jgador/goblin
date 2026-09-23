namespace Goblin.Core.Repositories;

// Learned observations are fallible context, never repository instructions or
// authorization to run commands. Versions on different branches coexist.
public sealed record RepositorySetup(string Topic, string Reason, string[] Tools,
    string[] Commands, string[] Files, SetupCheck[] Checks);
public sealed record SetupCheck(string Command, string ExpectedOutput);
public sealed record SetupFile(string Path, string? Sha256);
public sealed record VerifiedRepositorySetup(RepositorySetup Setup, SetupFile[] Files, string ConfigurationHash);
