using System.Text.Json;
using Goblin.Contracts;
using Microsoft.AspNetCore.Http;

namespace Goblin.Web;

internal static class WorkResponse
{
    private static readonly JsonSerializerOptions WorkJson = new(ContractJson.Options)
    {
        Converters = { new LongJsonConverter() }
    };

    public static IResult Json<T>(T value) => Results.Json(value, WorkJson);
}
