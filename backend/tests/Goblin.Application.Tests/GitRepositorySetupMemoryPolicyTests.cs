using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Goblin.Application.GitRepositories;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;
using Goblin.Core.GitRepositories;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class GitRepositorySetupMemoryPolicyTests
{
    private static readonly VerifiedGitRepositorySetup Observation = new(
        new("python-tests", "Tests need Python", ["python 3.12"], ["install-python"],
            ["pyproject.toml"], [new("python --version", "Python 3.12")]),
        [new("pyproject.toml", new string('b', 64))], new string('c', 64));

    [Fact]
    public async Task ValidRequestRoundTripsThroughBoundedReader()
    {
        var request = new SetupMemoryWrite(1, 2, "image-one", [Observation]);
        await using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request, ContractJson.Options));

        SetupMemoryWrite parsed = await GitRepositorySetupMemoryPolicy.ReadAsync(input, default);
        GitRepositorySetupMemoryPolicy.Validate(parsed);

        Assert.Equal("image-one", parsed.Environment);
        Assert.Equivalent(Observation, Assert.Single(parsed.Observations), strict: true);
    }

    [Fact]
    public async Task ReaderRejectsMalformedAndOversizedPayloads()
    {
        await Assert.ThrowsAsync<ApplicationFailure>(() => GitRepositorySetupMemoryPolicy.ReadAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("{")), default));
        await Assert.ThrowsAsync<ApplicationFailure>(() => GitRepositorySetupMemoryPolicy.ReadAsync(
            new MemoryStream(new byte[GitRepositorySetupRules.MaxPayloadBytes + 1]), default));
    }

    [Fact]
    public void ValidationRejectsUnsafeDuplicateAndInvalidObservations()
    {
        SetupMemoryWrite[] invalid =
        [
            new(0, 2, "image-one", [Observation]),
            new(1, 2, "", [Observation]),
            new(1, 2, "image-one", []),
            new(1, 2, "image-one", [Observation, Observation]),
            new(1, 2, "image-one", [Observation with { Setup = Observation.Setup with { Checks = [] } }])
        ];

        foreach (SetupMemoryWrite request in invalid)
            Assert.Equal("repository_setup_invalid",
                Assert.Throws<ApplicationFailure>(() => GitRepositorySetupMemoryPolicy.Validate(request)).Code);
    }

    [Fact]
    public void FingerprintIsStableAcrossFileOrderingAndChangesWithEvidence()
    {
        GitRepositorySetup setup = Observation.Setup with { Files = ["a.props", "b.props"] };
        var first = new VerifiedGitRepositorySetup(setup,
            [new("a.props", new string('a', 64)), new("b.props", new string('b', 64))], new string('c', 64));
        VerifiedGitRepositorySetup reordered = first with { Files = [first.Files[1], first.Files[0]] };
        VerifiedGitRepositorySetup changed = first with { ConfigurationHash = new string('d', 64) };

        Assert.Equal(GitRepositorySetupMemoryPolicy.Fingerprint(first), GitRepositorySetupMemoryPolicy.Fingerprint(reordered));
        Assert.NotEqual(GitRepositorySetupMemoryPolicy.Fingerprint(first), GitRepositorySetupMemoryPolicy.Fingerprint(changed));
    }

    [Fact]
    public void CanonicalComparisonIgnoresJsonFormattingButNotEvidenceChanges()
    {
        string serialized = JsonSerializer.Serialize(Observation,
            new JsonSerializerOptions(ContractJson.Options) { WriteIndented = true });

        Assert.True(GitRepositorySetupMemoryPolicy.Equivalent(serialized, Observation));
        Assert.False(GitRepositorySetupMemoryPolicy.Equivalent(serialized,
            Observation with { ConfigurationHash = new string('d', 64) }));
    }
}
