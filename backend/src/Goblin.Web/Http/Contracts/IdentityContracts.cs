using System.Text.Json.Serialization;
using Goblin.Application.Work;

namespace Goblin.Web.Http.Contracts;

public sealed class IdentityRequest
{
    [JsonConstructor]
    public IdentityRequest(IdentityKind[] kinds)
    {
        Kinds = kinds;
    }

    [JsonPropertyName("kinds")]
    public IdentityKind[] Kinds { get; init; }

    public Goblin.Application.Work.IdentityRequest ToApplication() =>
        new(Kinds);
}

public sealed class ReservedIdentities
{
    [JsonConstructor]
    public ReservedIdentities(long[] ids)
    {
        Ids = ids;
    }

    [JsonPropertyName("ids")]
    public long[] Ids { get; init; }

    public static ReservedIdentities From(Goblin.Application.Work.ReservedIdentities value) =>
        new(value.Ids);
}
