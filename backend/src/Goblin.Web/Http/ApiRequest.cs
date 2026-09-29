using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Goblin.Web;

internal static class ApiRequest
{
    public const string BodyKey = "goblin.body";

    public static Dictionary<string, JsonElement> Body(HttpContext context) =>
        (Dictionary<string, JsonElement>)context.Items[BodyKey]!;

    public static string? StringField(HttpContext context, string name)
    {
        Dictionary<string, JsonElement> body = Body(context);
        return body.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    public static async Task<Dictionary<string, JsonElement>> ReadBodyAsync(HttpRequest request)
    {
        if (request.ContentType?.Split(';')[0].Trim() != "application/json")
            throw new PublicError("invalid_content_type", "Send JSON content.", 415);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.HttpContext.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        byte[] buffer = new byte[8193];
        int size = 0;
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(buffer.AsMemory(size), timeout.Token)) > 0)
            {
                size += read;
                if (size > 8192) throw new PublicError("request_too_large", "The request is too large.", 413);
            }
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(buffer.AsSpan(0, size)) ?? throw new JsonException();
        }
        catch (JsonException) { throw new PublicError("invalid_json", "The request could not be read."); }
        catch (OperationCanceledException) { throw new PublicError("invalid_request", "The request was interrupted."); }
        catch (IOException) { throw new PublicError("invalid_request", "The request was interrupted."); }
    }
}
