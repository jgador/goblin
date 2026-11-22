import { AttentionReason } from "../api/values.js";
import type { RepositorySetupPayload, Work } from "./contracts.js";
import type { WorkspaceContext } from "./navigation.js";

export function isRepositoryHandoff(work: Work) {
    return (
        work.attention?.reason === AttentionReason.GitRepositoryRequired ||
        work.attention?.reason === AttentionReason.InputRequired
    );
}

export function repositoryApprovalKey(work?: Work) {
    const approval = work?.repositoryAuthorization;
    return approval ? `${approval.id}:${approval.status}` : "";
}

type SetupField = "github-repository" | "git-name" | "git-email";
const setupFields: readonly SetupField[] = [
    "github-repository",
    "git-name",
    "git-email",
];
type SetupErrors = Partial<Record<SetupField, string>>;

export function readRepositorySetup(
    form: HTMLFormElement,
):
    | { payload: RepositorySetupPayload; errors?: never }
    | { errors: SetupErrors; payload?: never } {
    const data = new FormData(form);
    let repository = String(data.get("github-repository") ?? "").trim();
    const url = repository.match(
        /^(?:https?:\/\/)?github\.com\/([^/]+\/[^/]+)/i,
    );
    if (url) repository = url[1].replace(/\.git$/, "");
    const gitAuthorName = String(data.get("git-name") ?? "").trim();
    const gitAuthorEmail = String(data.get("git-email") ?? "").trim();
    const policy = data.get("delivery-policy");
    const baseBranch = String(data.get("base-branch") ?? "").trim() || null;
    const errors: SetupErrors = {};
    if (!/^[a-zA-Z0-9_.-]+\/[a-zA-Z0-9_.-]+$/.test(repository))
        errors["github-repository"] =
            "Enter the full owner/repository name or GitHub URL.";
    const email = form.querySelector<HTMLInputElement>("#git-email")!;
    if (gitAuthorEmail && email.validity.typeMismatch)
        errors["git-email"] = "Enter a valid email for the agent’s commits.";
    if (Object.keys(errors).length) return { errors };
    const explicitDelivery =
        policy === "local" || policy === "push" || policy === "pr";
    if (!explicitDelivery && baseBranch)
        return {
            errors: {
                "github-repository":
                    "Choose Git actions when specifying a base branch.",
            },
        };
    return {
        payload: {
            repository: {
                repository,
                ...(gitAuthorName ? { gitAuthorName } : {}),
                ...(gitAuthorEmail ? { gitAuthorEmail } : {}),
            },
            ...(explicitDelivery
                ? {
                      delivery: {
                          baseBranch,
                          push: policy === "push" || policy === "pr",
                          openPullRequest: policy === "pr",
                      },
                  }
                : {}),
        },
    };
}

type SetupDraft = {
    approval: string;
    values: [string, string][];
    open: boolean;
    advancedOpen: boolean;
};

export class RepositoryDrafts {
    private readonly drafts = new Map<WorkspaceContext, SetupDraft>();
    private readonly errors = new Map<WorkspaceContext, SetupErrors>();

    remember(context: WorkspaceContext, draft: SetupDraft) {
        this.drafts.set(context, draft);
    }

    restore(context: WorkspaceContext, approval: string) {
        const draft = this.drafts.get(context);
        if (draft && draft.approval !== approval) {
            this.drafts.delete(context);
            this.errors.delete(context);
            return;
        }
        return draft;
    }

    showErrors(root: HTMLElement, context: WorkspaceContext) {
        const errors = this.errors.get(context);
        for (const id of setupFields) {
            const field = root.querySelector(`#${id}`);
            const message = root.querySelector<HTMLElement>(`#${id}-error`);
            if (!field || !message) continue;
            field.setAttribute("aria-invalid", String(!!errors?.[id]));
            message.textContent = errors?.[id] ?? "";
            message.hidden = !errors?.[id];
        }
    }

    setErrors(context: WorkspaceContext, errors: SetupErrors) {
        this.errors.set(context, errors);
    }

    clearError(context: WorkspaceContext, id: string) {
        const field = setupFields.find((field) => field === id);
        const errors = this.errors.get(context);
        if (errors && field) delete errors[field];
    }

    clear() {
        this.drafts.clear();
        this.errors.clear();
    }
}
