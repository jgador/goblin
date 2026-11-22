import { IdentityKind, WorkAction } from "../api/values.js";
import type {
    ConversationCommandPayload,
    SelectedWorkAction,
    View,
    WorkCommandPayload,
} from "./contracts.js";

type WorkRequest = { path: "/api/work/commands"; body: WorkCommandPayload };
type ConversationRequest = {
    path: "/api/conversations/commands";
    body: ConversationCommandPayload;
};
export type WorkCommandRequest = WorkRequest | ConversationRequest;

export class WorkCommands {
    constructor(
        private readonly reserve: (
            ...kinds: IdentityKind[]
        ) => Promise<string[]>,
    ) {}

    async create(text: string): Promise<WorkRequest> {
        const [commandId, workId] = await this.reserve(
            IdentityKind.Command,
            IdentityKind.Work,
        );
        return {
            path: "/api/work/commands",
            body: { commandId, workId, action: WorkAction.Create, text },
        };
    }

    async selected(
        item: View,
        action: SelectedWorkAction,
    ): Promise<WorkRequest> {
        const workId = item.work.id;
        const expectedVersion = item.version;
        const [commandId] = await this.reserve(IdentityKind.Command);
        return {
            path: "/api/work/commands",
            body: { ...action, commandId, workId, expectedVersion },
        };
    }

    async append(
        conversationId: string,
        text: string,
    ): Promise<ConversationRequest> {
        const [messageId, newConversationId] = conversationId
            ? await this.reserve(IdentityKind.Message)
            : await this.reserve(
                  IdentityKind.Message,
                  IdentityKind.Conversation,
              );
        return {
            path: "/api/conversations/commands",
            body: {
                conversationId: conversationId || newConversationId,
                messageId,
                text,
            },
        };
    }

    async track(conversationId: string): Promise<ConversationRequest> {
        const [workId, messageId] = await this.reserve(
            IdentityKind.Work,
            IdentityKind.Message,
        );
        return {
            path: "/api/conversations/commands",
            body: { conversationId, messageId, text: null, workId },
        };
    }
}
