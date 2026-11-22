import type { WorkspaceContext } from "./navigation.js";

export type DraftSnapshot = { context: WorkspaceContext; text: string };

export class ComposerDrafts {
    private readonly drafts = new Map<WorkspaceContext, DraftSnapshot>();

    get(context: WorkspaceContext) {
        return this.drafts.get(context)?.text ?? "";
    }

    set(context: WorkspaceContext, text: string) {
        if (text) this.drafts.set(context, { context, text });
        else this.drafts.delete(context);
    }

    capture(context: WorkspaceContext) {
        return this.drafts.get(context);
    }

    confirm(draft: DraftSnapshot) {
        if (this.drafts.get(draft.context) === draft)
            this.drafts.delete(draft.context);
    }

    clear() {
        this.drafts.clear();
    }
}
