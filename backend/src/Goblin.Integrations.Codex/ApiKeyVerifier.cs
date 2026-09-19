using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Goblin.Integrations.Codex;

public sealed class ApiKeyVerifier(HttpClient client)
{
    public static HttpClient CreateClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public async Task<string> VerifyAsync(string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead); }
        catch
        {
            throw new IntegrationFailure("verification_unavailable",
                "OpenAI could not be reached. Your key has not been saved. Please retry.");
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new IntegrationFailure("invalid_api_key", "OpenAI rejected this API key. Check the key and try again.");
            if (response.IsSuccessStatusCode) return "accepted";
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) return "unverified";
            throw new IntegrationFailure("verification_unavailable",
                "OpenAI could not verify the key. Your key has not been saved. Please retry.");
        }
    }
}
