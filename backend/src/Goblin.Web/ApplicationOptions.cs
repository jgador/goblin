using System;
using System.IO;
using System.Threading.Tasks;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;
using Goblin.Integrations.Codex;
using Goblin.Web.Monitoring;

namespace Goblin.Web;

public sealed record ApplicationOptions
{
    public string DataDirectory { get; init; } = ".goblin-auth";
    public string? PasswordHashFile { get; init; }
    public string PublicOrigin { get; init; } = "http://localhost:8787";
    public bool AllowInsecureHttp { get; init; }
    public string ListenUrl { get; init; } = "http://127.0.0.1:8787";
    public string AssetDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    public Func<CodexOptions, CodexOptions>? ConfigureCodex { get; init; }
    public Func<string, Task<VerificationState>>? VerifyApiKey { get; init; }
    public TimeSpan PromptTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public bool RecoverRuntime { get; init; } = true;
    public bool EnableWork { get; init; }
    public IExecutionHost? ExecutionHost { get; init; }
    public string GitHubCommand { get; init; } = "gh";
    public string? HeadlampUrl { get; init; }
    public string? VictoriaLogsUrl { get; init; }
    public ISystemSource? SystemSource { get; init; }
}
