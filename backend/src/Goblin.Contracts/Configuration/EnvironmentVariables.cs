namespace Goblin.Contracts.Configuration;

/// <summary>Environment names at configuration boundaries; no environment access or parsing.</summary>
/// <remarks>See docs/environment-variables.md before adding or forwarding a variable.</remarks>
public static class EnvironmentVariables
{
    /// <summary>Slack app-level token passed by the supported CLI deploy hook.</summary>
    /// <remarks>Format: xapp token. Required only inside the temporary setup hook; sensitive, never inherited from the operator.</remarks>
    public const string SlackAppToken = "SLACK_APP_TOKEN";
    /// <summary>Slack bot token passed by the supported CLI deploy hook.</summary>
    /// <remarks>Format: xoxb token. Required only inside the temporary setup hook; sensitive, never inherited from the operator.</remarks>
    public const string SlackBotToken = "SLACK_BOT_TOKEN";
    /// <summary>Disable optional telemetry in Goblin's temporary Slack CLI helper.</summary>
    /// <remarks>Format: true. Default/fallback: Goblin always sets true for setup. Required: Supplied only to the helper; not sensitive.</remarks>
    public const string SlackDisableTelemetry = "SLACK_DISABLE_TELEMETRY";
    /// <summary>Private temporary directory for setup subprocesses.</summary>
    /// <remarks>Format: Absolute directory. Default/fallback: operating-system temporary directory. Required: Set for isolated Slack setup; not sensitive.</remarks>
    public const string TmpDir = "TMPDIR";
    /// <summary>External credential used as a negative inheritance fixture; Goblin authenticates through private credential storage.</summary>
    /// <remarks>Format: Secret text. Default/fallback: Not forwarded to isolated runtimes or GitHub CLI.
    /// Required: Never required by Goblin environment configuration. Sensitive: yes.</remarks>
    public const string CodexApiKey = "CODEX_API_KEY";

    /// <summary>Isolated Codex configuration and credential directory.</summary>
    /// <remarks>Format: Private directory path. Default/fallback: Goblin supplies its private Codex directory; inherited values are replaced.
    /// Required: Supplied for Codex; required by the fake runtime fixture. Sensitive: yes.</remarks>
    public const string CodexHome = "CODEX_HOME";

    /// <summary>Application PostgreSQL connection; .NET maps __ to ConnectionStrings:Goblin.</summary>
    /// <remarks>Format: Npgsql connection string. Default/fallback: Existing appsettings/configuration providers; environment overrides JSON under the standard .NET host.
    /// Required: Required for durable Work. Sensitive: yes.</remarks>
    public const string ConnectionStringsGoblin = "ConnectionStrings__Goblin";

    /// <summary>Schema administrator PostgreSQL connection; .NET maps __ to ConnectionStrings:GoblinAdmin.</summary>
    /// <remarks>Format: Npgsql connection string. Default/fallback: Existing database tooling appsettings/configuration providers; environment overrides JSON.
    /// Required: Required for schema migration. Sensitive: yes.</remarks>
    public const string ConnectionStringsGoblinAdmin = "ConnectionStrings__GoblinAdmin";

    /// <summary>Fictional external-service credential used only to test subprocess environment isolation.</summary>
    /// <remarks>Format: Dummy test marker. Default/fallback: Not forwarded to isolated runtimes or GitHub CLI.
    /// Required: Never required by Goblin environment configuration. Sensitive: yes.</remarks>
    public const string ExternalServiceApiKey = "EXTERNAL_SERVICE_API_KEY";

    /// <summary>Private GitHub CLI configuration directory.</summary>
    /// <remarks>Format: Directory path. Default/fallback: Goblin supplies its private GitHub profile.
    /// Required: Supplied for GitHub CLI. Sensitive: yes.</remarks>
    public const string GhConfigDir = "GH_CONFIG_DIR";

    /// <summary>Disable GitHub CLI update notifications.</summary>
    /// <remarks>Format: 1. Default/fallback: Goblin supplies 1.
    /// Required: Supplied for GitHub CLI. Sensitive: no.</remarks>
    public const string GhNoUpdateNotifier = "GH_NO_UPDATE_NOTIFIER";

    /// <summary>Disable GitHub CLI interactive prompts.</summary>
    /// <remarks>Format: 1. Default/fallback: Goblin supplies 1.
    /// Required: Supplied for GitHub CLI. Sensitive: no.</remarks>
    public const string GhPromptDisabled = "GH_PROMPT_DISABLED";

    /// <summary>External GitHub credential used by the negative inheritance fixture.</summary>
    /// <remarks>Format: Secret text. Default/fallback: Not forwarded to GitHub CLI; Goblin uses private credential storage.
    /// Required: Never required by Goblin environment configuration. Sensitive: yes.</remarks>
    public const string GhToken = "GH_TOKEN";

    /// <summary>Replace user-wide Git configuration in isolated workers.</summary>
    /// <remarks>Format: Filesystem path. Default/fallback: Goblin supplies /dev/null.
    /// Required: Supplied for Git/GitHub workers. Sensitive: no.</remarks>
    public const string GitConfigGlobal = "GIT_CONFIG_GLOBAL";

    /// <summary>Ignore machine-wide Git configuration in isolated workers.</summary>
    /// <remarks>Format: 1. Default/fallback: Goblin supplies 1.
    /// Required: Supplied for Git/GitHub workers. Sensitive: no.</remarks>
    public const string GitConfigNosystem = "GIT_CONFIG_NOSYSTEM";

    /// <summary>Disable Git credential prompts in noninteractive workers.</summary>
    /// <remarks>Format: 0. Default/fallback: Goblin supplies 0.
    /// Required: Supplied for Git/GitHub workers. Sensitive: no.</remarks>
    public const string GitTerminalPrompt = "GIT_TERMINAL_PROMPT";

    /// <summary>External GitHub credential used by the negative inheritance fixture.</summary>
    /// <remarks>Format: Secret text. Default/fallback: Not forwarded to GitHub CLI; Goblin uses private credential storage.
    /// Required: Never required by Goblin environment configuration. Sensitive: yes.</remarks>
    public const string GitHubToken = "GITHUB_TOKEN";

    /// <summary>Explicit opt-in to a non-loopback HTTP public origin.</summary>
    /// <remarks>Format: Case-insensitive true enables it. Default/fallback: false.
    /// Required: Required only for remote HTTP. Sensitive: no.</remarks>
    public const string GoblinAllowInsecureHttp = "GOBLIN_ALLOW_INSECURE_HTTP";

    /// <summary>Override the Codex executable.</summary>
    /// <remarks>Format: Executable path or command name. Default/fallback: Pinned npm binary when present, otherwise codex on PATH.
    /// Required: Optional. Sensitive: no.</remarks>
    public const string GoblinCodexCommand = "GOBLIN_CODEX_COMMAND";

    /// <summary>Private application data directory.</summary>
    /// <remarks>Format: Filesystem path. Default/fallback: .goblin-auth relative to the working directory.
    /// Required: Optional. Sensitive: no.</remarks>
    public const string GoblinDataDir = "GOBLIN_DATA_DIR";

    /// <summary>Container image used by isolated repository workers.</summary>
    /// <remarks>Format: Container image reference. Default/fallback: goblin-auth:0.1.0; deployment supplies the selected image.
    /// Required: Optional when isolated execution is enabled. Sensitive: no.</remarks>
    public const string GoblinExecutionImage = "GOBLIN_EXECUTION_IMAGE";

    /// <summary>Namespace for isolated repository executions and the repository listener.</summary>
    /// <remarks>Format: Kubernetes namespace. Default/fallback: Unset: local text execution only; monitoring independently defaults to agents.
    /// Required: Required for isolated repository execution. Sensitive: no.</remarks>
    public const string GoblinExecutionNamespace = "GOBLIN_EXECUTION_NAMESPACE";

    /// <summary>Private Headlamp origin exposed through the authenticated proxy.</summary>
    /// <remarks>Format: Internal HTTP origin without a path. Default/fallback: Proxy disabled when unset.
    /// Required: Required only for the Headlamp proxy. Sensitive: no.</remarks>
    public const string GoblinHeadlampUrl = "GOBLIN_HEADLAMP_URL";

    /// <summary>Web listener host.</summary>
    /// <remarks>Format: IP address or host accepted by Kestrel. Default/fallback: 127.0.0.1.
    /// Required: Optional. Sensitive: no.</remarks>
    public const string GoblinHost = "GOBLIN_HOST";

    /// <summary>CA certificate file for Kubernetes API verification.</summary>
    /// <remarks>Format: Filesystem path to PEM certificates. Default/fallback: /var/run/secrets/kubernetes.io/serviceaccount/ca.crt.
    /// Required: Required file when Kubernetes access is enabled. Sensitive: no.</remarks>
    public const string GoblinKubernetesCaFile = "GOBLIN_KUBERNETES_CA_FILE";

    /// <summary>Service account bearer-token file for Kubernetes API access.</summary>
    /// <remarks>Format: Filesystem path. Default/fallback: /var/run/secrets/kubernetes.io/serviceaccount/token.
    /// Required: Required file when Kubernetes access is enabled. Sensitive: yes.</remarks>
    public const string GoblinKubernetesTokenFile = "GOBLIN_KUBERNETES_TOKEN_FILE";

    /// <summary>Kubernetes API origin for monitoring and sandbox hosting.</summary>
    /// <remarks>Format: Absolute HTTPS URL. Default/fallback: https://kubernetes.default.svc.
    /// Required: Optional in a cluster. Sensitive: no.</remarks>
    public const string GoblinKubernetesUrl = "GOBLIN_KUBERNETES_URL";

    /// <summary>Maximum cached workspace volume capacity.</summary>
    /// <remarks>Format: Positive integer. Default/fallback: 4; invalid or nonpositive values fail configuration.
    /// Required: Optional. Sensitive: no.</remarks>
    public const string GoblinMaxCachedWorkspaces = "GOBLIN_MAX_CACHED_WORKSPACES";

    /// <summary>Maximum active sandbox capacity.</summary>
    /// <remarks>Format: Positive integer. Default/fallback: 2; invalid or nonpositive values fail configuration.
    /// Required: Optional. Sensitive: no.</remarks>
    public const string GoblinMaxSandboxes = "GOBLIN_MAX_SANDBOXES";

    /// <summary>Namespace containing the Goblin application.</summary>
    /// <remarks>Format: Kubernetes namespace. Default/fallback: goblin.
    /// Required: Optional when Kubernetes monitoring is enabled. Sensitive: no.</remarks>
    public const string GoblinNamespace = "GOBLIN_NAMESPACE";

    /// <summary>Node whose Kubernetes resource usage is monitored.</summary>
    /// <remarks>Format: Kubernetes node name. Default/fallback: Kubernetes monitoring source disabled when unset.
    /// Required: Required for Kubernetes node monitoring. Sensitive: no.</remarks>
    public const string GoblinNodeName = "GOBLIN_NODE_NAME";

    /// <summary>File containing the owner password verifier; never a plaintext password.</summary>
    /// <remarks>Format: Path to a private PBKDF2-SHA256 verifier file. Default/fallback: Web requires an explicit file; dev/install tooling uses .goblin-secrets/owner-password and validates explicit overrides without fallback.
    /// Required: Required by the web host; optional override for tooling. Sensitive: yes.</remarks>
    public const string GoblinPasswordHashFile = "GOBLIN_PASSWORD_HASH_FILE";

    /// <summary>Web listener port.</summary>
    /// <remarks>Format: Integer from 1 to 65535. Default/fallback: 8787; invalid values fail startup.
    /// Required: Optional. Sensitive: no.</remarks>
    public const string GoblinPort = "GOBLIN_PORT";

    /// <summary>Browser-facing origin used for origin validation and deployment.</summary>
    /// <remarks>Format: Absolute HTTPS origin, or loopback HTTP; remote HTTP requires explicit opt-in. Default/fallback: Web: http://localhost:&lt;GOBLIN_PORT&gt;; installer: http://&lt;hostname&gt;; local test sets its configured origin.
    /// Required: Optional. Sensitive: no.</remarks>
    public const string GoblinPublicOrigin = "GOBLIN_PUBLIC_ORIGIN";

    /// <summary>Internal repository broker origin for isolated workers.</summary>
    /// <remarks>Format: Absolute HTTP URL reachable inside the cluster. Default/fallback: http://goblin-repository.goblin.svc:8788.
    /// Required: Optional when isolated execution is enabled. Sensitive: no.</remarks>
    public const string GoblinRepositoryUrl = "GOBLIN_REPOSITORY_URL";

    /// <summary>CPU resource limit per repository sandbox.</summary>
    /// <remarks>Format: Kubernetes CPU quantity. Default/fallback: 2.
    /// Required: Optional when isolated execution is enabled. Sensitive: no.</remarks>
    public const string GoblinSandboxCpuLimit = "GOBLIN_SANDBOX_CPU_LIMIT";

    /// <summary>Memory resource limit per repository sandbox.</summary>
    /// <remarks>Format: Kubernetes memory quantity. Default/fallback: 2Gi.
    /// Required: Optional when isolated execution is enabled. Sensitive: no.</remarks>
    public const string GoblinSandboxMemoryLimit = "GOBLIN_SANDBOX_MEMORY_LIMIT";

    /// <summary>Synthetic secret used to verify that unrelated environment entries are not inherited.</summary>
    /// <remarks>Format: Test-only text. Default/fallback: Never forwarded.
    /// Required: Tests only. Sensitive: yes.</remarks>
    public const string GoblinSecret = "GOBLIN_SECRET";

    /// <summary>Opt-in real PostgreSQL test administrator connection.</summary>
    /// <remarks>Format: Npgsql connection string. Default/fallback: Database tests skip when the required test connections are absent.
    /// Required: Required only for real PostgreSQL tests. Sensitive: yes.</remarks>
    public const string GoblinTestPostgresAdmin = "GOBLIN_TEST_POSTGRES_ADMIN";

    /// <summary>Opt-in real PostgreSQL test application connection, forwarded as ConnectionStrings__Goblin by the HTTP harness.</summary>
    /// <remarks>Format: Npgsql connection string. Default/fallback: Database/HTTP/browser Work tests skip when absent.
    /// Required: Required only for real PostgreSQL tests. Sensitive: yes.</remarks>
    public const string GoblinTestPostgresApp = "GOBLIN_TEST_POSTGRES_APP";

    /// <summary>Private VictoriaLogs origin exposed through the authenticated logs proxy.</summary>
    /// <remarks>Format: Internal HTTP origin; upstream uses -http.pathPrefix=/logs. Default/fallback: Proxy disabled when unset.
    /// Required: Required only for the logs proxy. Sensitive: no.</remarks>
    public const string GoblinVictorialogsUrl = "GOBLIN_VICTORIALOGS_URL";

    /// <summary>Enable durable Work services.</summary>
    /// <remarks>Format: Case-insensitive false disables them. Default/fallback: true.
    /// Required: Optional; enabled Work requires PostgreSQL. Sensitive: no.</remarks>
    public const string GoblinWorkEnabled = "GOBLIN_WORK_ENABLED";

    /// <summary>Unix home directory for isolated runtime and CLI configuration.</summary>
    /// <remarks>Format: Directory path. Default/fallback: Goblin supplies a private home for Codex, GitHub, and sandbox workers.
    /// Required: Supplied for isolated subprocesses. Sensitive: no.</remarks>
    public const string Home = "HOME";

    /// <summary>Proxy URL allowed through to Codex.</summary>
    /// <remarks>Format: Proxy URL, possibly including credentials. Default/fallback: Inherited only when present.
    /// Required: Optional. Sensitive: yes.</remarks>
    public const string HttpProxy = "HTTP_PROXY";

    /// <summary>Proxy URL allowed through to Codex.</summary>
    /// <remarks>Format: Proxy URL, possibly including credentials. Default/fallback: Inherited only when present.
    /// Required: Optional. Sensitive: yes.</remarks>
    public const string HttpsProxy = "HTTPS_PROXY";

    /// <summary>Subprocess locale.</summary>
    /// <remarks>Format: Locale name. Default/fallback: Codex inherits only when present; local text worker sets C.UTF-8.
    /// Required: Optional. Sensitive: no.</remarks>
    public const string Lang = "LANG";

    /// <summary>Subprocess locale override.</summary>
    /// <remarks>Format: Locale name. Default/fallback: Codex inherits only when present; Git/GitHub helpers set C.
    /// Required: Optional. Sensitive: no.</remarks>
    public const string LcAll = "LC_ALL";

    /// <summary>Disable GitHub CLI ANSI color output.</summary>
    /// <remarks>Format: 1. Default/fallback: Goblin supplies 1.
    /// Required: Supplied for GitHub CLI. Sensitive: no.</remarks>
    public const string NoColor = "NO_COLOR";

    /// <summary>Destinations bypassing the proxy, allowed through to Codex.</summary>
    /// <remarks>Format: Comma-separated hosts or address patterns. Default/fallback: Inherited only when present.
    /// Required: Optional. Sensitive: no.</remarks>
    public const string NoProxy = "NO_PROXY";

    /// <summary>Additional certificate trust location allowed through to Codex.</summary>
    /// <remarks>Format: PEM certificate file path. Default/fallback: Inherited only when present; otherwise runtime trust defaults.
    /// Required: Optional. Sensitive: no.</remarks>
    public const string NodeExtraCaCerts = "NODE_EXTRA_CA_CERTS";

    /// <summary>External credential used as a negative inheritance fixture; Goblin authenticates through private credential storage.</summary>
    /// <remarks>Format: Secret text. Default/fallback: Not forwarded to isolated runtimes or GitHub CLI.
    /// Required: Never required by Goblin environment configuration. Sensitive: yes.</remarks>
    public const string OpenaiApiKey = "OPENAI_API_KEY";

    /// <summary>Executable search path for application tools and subprocesses.</summary>
    /// <remarks>Format: OS path-separator-delimited directories. Default/fallback: Inherited when allowed; GitHub/sandbox workers use /usr/bin:/bin if absent; Codex lookup uses empty path if absent.
    /// Required: Required when executables are not absolute paths. Sensitive: no.</remarks>
    public const string Path = "PATH";

    /// <summary>Windows executable extensions allowed through to Codex.</summary>
    /// <remarks>Format: Semicolon-separated extensions. Default/fallback: Inherited only when present.
    /// Required: Optional. Sensitive: no.</remarks>
    public const string Pathext = "PATHEXT";

    /// <summary>Additional certificate trust location allowed through to Codex.</summary>
    /// <remarks>Format: Directory path. Default/fallback: Inherited only when present; otherwise runtime trust defaults.
    /// Required: Optional. Sensitive: no.</remarks>
    public const string SslCertDir = "SSL_CERT_DIR";

    /// <summary>Additional certificate trust location allowed through to Codex.</summary>
    /// <remarks>Format: PEM certificate file path. Default/fallback: Inherited only when present; otherwise runtime trust defaults.
    /// Required: Optional. Sensitive: no.</remarks>
    public const string SslCertFile = "SSL_CERT_FILE";

    /// <summary>Windows system directory allowed through to Codex.</summary>
    /// <remarks>Format: Directory path. Default/fallback: Inherited only when present.
    /// Required: Required by some Windows runtime operations. Sensitive: no.</remarks>
    public const string SystemRoot = "SystemRoot";

    /// <summary>Subprocess timezone.</summary>
    /// <remarks>Format: Timezone identifier. Default/fallback: Codex inherits only when present; otherwise OS default.
    /// Required: Optional. Sensitive: no.</remarks>
    public const string Tz = "TZ";

    /// <summary>Windows home directory for isolated Codex configuration.</summary>
    /// <remarks>Format: Directory path. Default/fallback: Goblin supplies the same private home as HOME.
    /// Required: Supplied for Codex. Sensitive: no.</remarks>
    public const string Userprofile = "USERPROFILE";

    /// <summary>Windows system directory allowed through to Codex.</summary>
    /// <remarks>Format: Directory path. Default/fallback: Inherited only when present.
    /// Required: Required by some Windows runtime operations. Sensitive: no.</remarks>
    public const string Windir = "WINDIR";
}
