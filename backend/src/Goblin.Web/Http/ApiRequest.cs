using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Goblin.Web;

internal static class ApiRequest
{
    public const string BodyKey = "goblin.body";

    public static T Body<T>(HttpContext context, JsonSerializerOptions? options = null) where T : class =>
        JsonSerializer.Deserialize<T>((byte[])context.Items[BodyKey]!, options) ?? throw new JsonException();

    public static async Task<T> ReadBodyAsync<T>(HttpRequest request, JsonSerializerOptions? options = null) where T : class =>
        JsonSerializer.Deserialize<T>(await ReadBodyAsync(request), options) ?? throw new JsonException();

    public static async Task<byte[]> ReadBodyAsync(HttpRequest request)
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
            ValidateObject(buffer.AsSpan(0, size));
            return buffer.AsSpan(0, size).ToArray();
        }
        catch (JsonException) { throw new PublicError("invalid_json", "The request could not be read."); }
        catch (OperationCanceledException) { throw new PublicError("invalid_request", "The request was interrupted."); }
        catch (IOException) { throw new PublicError("invalid_request", "The request was interrupted."); }
    }

    // Validate syntax once, including bodyless POSTs, without building a JSON DOM.
    // Each endpoint binds the buffered bytes directly to its own wire contract.
    private static void ValidateObject(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
        reader.Skip();
        if (reader.Read()) throw new JsonException();
    }
}
