using System.Text.Json.Serialization;
using Goblin.Protocol;

namespace Goblin.Web;

// Goblin's public HTTP contract intentionally exposes only account summaries and final replies.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ApiKeyAccountView), "apiKey")]
[JsonDerivedType(typeof(ChatgptAccountView), "chatgpt")]
public abstract record AccountView;
public sealed record ApiKeyAccountView : AccountView;
public sealed record ChatgptAccountView(string? Email, PlanType? PlanType) : AccountView;
public sealed record DeviceLogin(string Id, string VerificationUrl, string UserCode);
public sealed record Notice(string Kind, string Message);
public sealed record AuthenticationState(AccountView? Account, DeviceLogin? Login, Notice? Notice,
    string? Verification, bool RuntimeReady);
public sealed record PromptResult(string Reply, string Model, long DurationMs, string AuthType);
public sealed record SessionState(bool Authenticated);
public sealed record ApiFailure(ErrorView Error);
public sealed record ErrorView(string Code, string Message);
