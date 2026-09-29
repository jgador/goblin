using System.Text.Json;
using Goblin.Application.Work;
using Microsoft.AspNetCore.Http;

namespace Goblin.Web;

internal static class WorkResponse
{
    private static readonly JsonSerializerOptions WorkJson = new(WorkStore.Json)
    {
        Converters = { new LongJsonConverter() }
    };

    public static IResult Json<T>(T value) => Results.Json(value, WorkJson);
}
