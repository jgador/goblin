using System;
using Goblin.Application.Work;
using Goblin.Core.Work;
using Goblin.Integrations.Codex;

namespace Goblin.Web;

public sealed class PublicError : Exception
{
    public PublicError(string code, string message, int status = 400) : base(message)
    {
        Code = code;
        Status = status;
    }

    public string Code { get; }
    public int Status { get; }

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
            "workspace_checkpoint_unconfirmed" => "The Git checkpoint could not be verified. The local workspace is retained.",
            "workspace_unavailable" => "This workspace is unavailable. Check its retained volume and inspection session.",
            "workspace_session_exists" => "A workspace session is already open for this Work.",
            "workspace_file_too_large" => "This file is too large to preview. Use the workspace terminal to inspect it.",
            "workspace_not_found" => "This saved workspace or file could not be found.",
            "workspace_changed" => "The workspace changed. Refresh before continuing.",
            "work_changed" => "This work changed. Refresh it before submitting another action.",
            "command_id_reused" => "That command identifier was already used for another action.",
            "connection_in_use" => "This connection is in use. Stop or reconcile its execution before changing accounts.",
            "connection_not_found" => "This AI connection could not be found.",
            "models_unavailable" => "Model choices are unavailable for this connection. Refresh models or use the runtime default.",
            "model_unavailable" => "The selected model is no longer listed. Refresh models and choose another.",
            "reasoning_effort_unavailable" => "The selected reasoning effort is unavailable for this model.",
            "invalid_model_selection" => "Choose a listed model and reasoning effort.",
            "github_connection_in_use" => "GitHub is in use. Open Work to cancel or reconcile queued, active, or cleanup-pending repository work before changing this connection.",
            "repository_unavailable" => "Check the repository name and connected GitHub account, then review access again.",
            "repository_requires_new_work" => "This Work already has a repository. Use its full name to continue, or start separate Work for another repository.",
            "repository_ambiguous" => "More than one repository may match. Enter the full owner/repository name.",
            "repository_intent_conflict" => "The Git actions conflict. Clarify whether to push or open a PR, and which base branch to use.",
            "repository_authorization_changed" => "Repository access changed. Review a new authorization request before continuing.",
            "repository_operation_unavailable" => "This repository operation is unavailable. Inspect Work and reconcile its execution.",
            "prompt_in_progress" => "A verification prompt is already running.",
            "work_not_found" => "This work could not be found.",
            "agent_required" => "Assign an agent before starting work.",
            _ => "This command could not be accepted. Refresh the work and check the requested action."
        }, failure.Code is "work_not_found" or "connection_not_found" ? 404 :
            failure.Code == "invalid_model_selection" ? 400 : 409),
        WorkRuleException failure => new("work_rule_" + failure.Rule, failure.Rule == WorkRule.ReconciliationRequired
            ? "Reconcile the previous execution before retrying."
            : "This action is not available in the current work state.", 409),
        _ => new("internal_error", "The command could not be confirmed. Refresh to check its saved state.", 500)
    };

    public static PublicError RuntimeUnavailable() => new("runtime_unavailable",
        "Codex is unavailable. Retry in a moment. If this continues, restart Goblin.", 503);
}
