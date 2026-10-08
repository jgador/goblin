using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;
using Goblin.Core.GitRepositories;

namespace Goblin.Application.GitRepositories;

internal static class GitRepositorySetupMemoryPolicy
{
    internal static void Validate(SetupMemoryWrite request)
    {
        if (!GitRepositorySetupRules.SafeText(request.Environment, 1000) || request.TurnNumber <= 0 ||
            request.Observations is not { Length: > 0 and <= GitRepositorySetupRules.MaxObservations } ||
            !request.Observations.All(GitRepositorySetupRules.Valid) ||
            request.Observations.Select(x => x.Setup.Topic).Distinct(StringComparer.Ordinal).Count() != request.Observations.Length ||
            JsonSerializer.SerializeToUtf8Bytes(request, ContractJson.Options).Length > GitRepositorySetupRules.MaxPayloadBytes)
            throw new ApplicationFailure("repository_setup_invalid");
    }

    internal static async Task<SetupMemoryWrite> ReadAsync(Stream input, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096]; int count;
        while ((count = await input.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + count > GitRepositorySetupRules.MaxPayloadBytes)
                throw new ApplicationFailure("repository_setup_invalid");
            await buffer.WriteAsync(chunk.AsMemory(0, count), token);
        }
        try
        {
            return JsonSerializer.Deserialize<SetupMemoryWrite>(buffer.ToArray(), ContractJson.Options) ?? throw new JsonException();
        }
        catch (JsonException) { throw new ApplicationFailure("repository_setup_invalid"); }
    }

    internal static string Fingerprint(VerifiedGitRepositorySetup observation) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { observation.ConfigurationHash, Files = observation.Files.OrderBy(x => x.Path, StringComparer.Ordinal) }, ContractJson.Options)));

    internal static bool Equivalent(string serialized, VerifiedGitRepositorySetup observation) =>
        JsonSerializer.Serialize(JsonSerializer.Deserialize<VerifiedGitRepositorySetup>(serialized, ContractJson.Options), ContractJson.Options) ==
        JsonSerializer.Serialize(observation, ContractJson.Options);
}
