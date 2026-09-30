using System.Text.Json.Serialization;

namespace Goblin.Web;

public sealed class SessionState
{
    [JsonConstructor]
    public SessionState(bool authenticated)
    {
        Authenticated = authenticated;
    }

    [JsonPropertyName("authenticated")]
    public bool Authenticated { get; init; }
}

public sealed class ApiFailure
{
    [JsonConstructor]
    public ApiFailure(ErrorView error)
    {
        Error = error;
    }

    [JsonPropertyName("error")]
    public ErrorView Error { get; init; }
}

public sealed class ErrorView
{
    [JsonConstructor]
    public ErrorView(string code, string message)
    {
        Code = code;
        Message = message;
    }

    [JsonPropertyName("code")]
    public string Code { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; }
}
