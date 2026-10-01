import { WorkAction, isContractValue } from "../api/values.js";

export type PendingCommand = {
    path: "/api/work/commands" | "/api/conversations/commands";
    body: Record<string, unknown>;
};
type CommandStorage = Pick<Storage, "getItem" | "setItem" | "removeItem">;
const storageKey = "goblin.pendingCommand";

function record(value: unknown): value is Record<string, unknown> {
    return !!value && typeof value === "object" && !Array.isArray(value);
}
function identity(value: unknown) {
    return typeof value === "string" && /^[1-9]\d*$/.test(value);
}
function restorable(value: unknown): value is PendingCommand {
    if (!record(value) || !record(value.body)) return false;
    const body = value.body;
    // Preserve the whole original payload, including decimal identities and version.
    // The backend validates command fields and owns deduplication and lifecycle rules.
    return value.path === "/api/work/commands"
        ? identity(body.commandId) &&
              identity(body.workId) &&
              isContractValue(WorkAction, body.action)
        : value.path === "/api/conversations/commands" &&
              identity(body.conversationId) &&
              identity(body.messageId);
}

export class CommandSubmission {
    private readonly storage: CommandStorage;
    private readonly dispatch: (command: PendingCommand) => Promise<unknown>;
    private readonly changed: () => void;
    private command: PendingCommand | null = null;
    private inFlight = false;
    readonly recoveryNotice: string;

    constructor(
        storage: CommandStorage,
        dispatch: (command: PendingCommand) => Promise<unknown>,
        changed: () => void,
    ) {
        this.storage = storage;
        this.dispatch = dispatch;
        this.changed = changed;
        let recoveryNotice = "";
        try {
            const saved = storage.getItem(storageKey);
            if (saved !== null) {
                const value: unknown = JSON.parse(saved);
                if (!restorable(value)) throw new Error();
                this.command = value;
            }
        } catch {
            recoveryNotice =
                "The saved command could not be recovered. Inspect saved Work before submitting another command.";
            try {
                storage.removeItem(storageKey);
            } catch {
                /* Storage may be unavailable. */
            }
        }
        this.recoveryNotice = recoveryNotice;
    }

    get pending() {
        return this.command;
    }
    get sending() {
        return this.inFlight;
    }

    submit(command: PendingCommand | (() => Promise<PendingCommand>)) {
        if (this.inFlight || this.command) return Promise.resolve(undefined);
        return this.send(
            typeof command === "function" ? command : async () => command,
        );
    }

    resend() {
        const command = this.command;
        if (this.inFlight || !command) return Promise.resolve(undefined);
        return this.send(async () => command);
    }

    dismiss() {
        if (this.inFlight) return;
        this.storage.removeItem(storageKey);
        this.command = null;
        this.changed();
    }

    private async send(prepare: () => Promise<PendingCommand>) {
        this.inFlight = true;
        this.changed();
        try {
            const command = await prepare();
            // Persist before dispatch; a storage failure must never send an untracked command.
            this.storage.setItem(storageKey, JSON.stringify(command));
            this.command = command;
            this.changed();
            await this.dispatch(command);
            this.storage.removeItem(storageKey);
            this.command = null;
            return command;
        } finally {
            this.inFlight = false;
            this.changed();
        }
    }
}
