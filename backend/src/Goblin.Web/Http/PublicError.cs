using System;
using Goblin.Integrations.Codex;
using Goblin.Application.Work;
using Goblin.Core.Work;

namespace Goblin.Web;

public sealed class PublicError(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;

    public static PublicError Translate(Exception error) => error switch
    {
        PublicError known => known,
        System.Text.Json.JsonException => new("invalid_command", "The command could not be read."),
        Goblin.Integrations.GitHub.GitHubFailure failure => new("github_connection_failed", failure.Message, 502),
        IntegrationFailure failure => new(failure.Code, failure.Message, failure.Code switch
        {
            "runtime_unavailable" or "unexpected_provider" => 503,
            "runtime_timeout" or "prompt_timeout" => 504,
            "prompt_cancelled" => 408,
            "prompt_limit_reached" => 429,
            "already_connected" or "login_in_progress" or "not_connected" or "prompt_in_progress" => 409,
            "invalid_api_key" or "invalid_prompt" => 400,
            _ => 502
        }),
        ApplicationFailure failure => new(failure.Code, failure.Code switch
        {
            "work_changed" => "This work changed. Refresh it before submitting another action.",
            "command_id_reused" => "That command identifier was already used for another action.",
            "connection_in_use" => "This connection is in use. Stop or reconcile its execution before changing accounts.",
            "prompt_in_progress" => "A verification prompt is already running.",
            "work_not_found" => "This work could not be found.",
            "agent_required" => "Assign an agent before starting work.",
            _ => "This command could not be accepted. Refresh the work and check the requested action."
        }, failure.Code == "work_not_found" ? 404 : 409),
        WorkRuleException failure => new("work_rule_" + failure.Rule, failure.Rule == WorkRule.ReconciliationRequired
            ? "Reconcile the previous execution before retrying."
            : "This action is not available in the current work state.", 409),
        _ => new("internal_error", "The command could not be confirmed. Refresh to check its saved state.", 500)
    };

    public static PublicError RuntimeUnavailable() => new("runtime_unavailable",
        "Codex is unavailable. Retry in a moment. If this continues, restart Goblin.", 503);
}
