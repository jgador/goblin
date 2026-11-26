import type { WorkspaceContext } from "./navigation.js";

export type DraftMode = "message" | `review:${string}`;
const draftKey = (context: WorkspaceContext, mode: DraftMode) =>
    `${context}/${mode}`;
export type DraftSnapshot = {
    context: WorkspaceContext;
    mode: DraftMode;
    text: string;
};

export class ComposerDrafts {
    private readonly drafts = new Map<string, DraftSnapshot>();

    get(context: WorkspaceContext, mode: DraftMode = "message") {
        return this.capture(context, mode)?.text ?? "";
    }

    set(context: WorkspaceContext, text: string, mode: DraftMode = "message") {
        const key = draftKey(context, mode);
        if (text) this.drafts.set(key, { context, mode, text });
        else this.drafts.delete(key);
    }

    capture(context: WorkspaceContext, mode: DraftMode = "message") {
        return this.drafts.get(draftKey(context, mode));
    }

    confirm(draft: DraftSnapshot) {
        const key = draftKey(draft.context, draft.mode);
        if (this.drafts.get(key) === draft) this.drafts.delete(key);
    }

    clear() {
        this.drafts.clear();
    }
}
