import type {
    WorkStatus,
    AttemptStatus,
    AttentionReason,
    FailureKind,
    WorkEventKind,
    RepositoryAuthorizationStatus,
} from "../api/values.js";
// Goblin-owned Work HTTP views. IDs remain decimal strings.
export type Attempt = {
    id: string;
    status: AttemptStatus;
    agentId?: string;
    queuedAt?: string;
    startedAt?: string;
    finishedAt?: string;
    target: {
        runtime: string;
        requestedModel?: string;
        requestedEffort?: string;
        repository?: {
            repository: string;
            gitAuthorName?: string;
            gitAuthorEmail?: string;
            grant?: {
                login: string;
                branch: string;
                baseBranch: string;
                allowPush: boolean;
                allowPullRequest: boolean;
            };
        };
    };
    session?: { model?: string };
    failure?: FailureKind;
    environmentReference?: string;
    cleanupPending?: boolean;
    turnNumber?: number;
    workspaceNumber?: number;
    checkpointId?: string;
    releaseWorkspace?: boolean;
    priorTurns?: {
        number: number;
        workspaceNumber: number;
        startedAt?: string;
        finishedAt?: string;
        session?: { model?: string };
        checkpointId?: string;
    }[];
};
export type Work = {
    id: string;
    objective: string;
    status: WorkStatus;
    agentId?: string;
    attention?: { reason: AttentionReason; failure?: FailureKind };
    repositoryRequest?: {
        repositories: string[];
        target: Attempt["target"];
    };
    repositoryAuthorization?: {
        id: string;
        status: RepositoryAuthorizationStatus;
        target: Attempt["target"];
        enableRepository: boolean;
        retry: boolean;
    };
    attempts: Attempt[];
    history: {
        sequence: string;
        kind: WorkEventKind;
        text?: string;
        failure?: FailureKind;
        occurredAt: string;
        attemptId?: string;
        decisionId?: string;
    }[];
    decisions: {
        id: string;
        attemptId?: string;
        question: string;
        answer?: string;
        requestedAt?: string;
        answeredAt?: string;
    }[];
    results: {
        attemptId: string;
        text: string;
        proposedAt?: string;
        approvedAt?: string;
        requestedChanges?: string;
    }[];
    artifacts: {
        name: string;
        reference: string;
        attemptId?: string;
        createdAt?: string;
    }[];
};
export type View = {
    version: string;
    createdAt: string;
    updatedAt: string;
    work: Work;
};
export type Conversation = {
    id: string;
    title: string;
    workId?: string;
    messages: {
        id: string;
        text: string;
        createdAt: string;
        source?: {
            provider: string;
            userId: string;
            workspaceId: string;
            channelId: string;
            threadId: string;
            messageId: string;
        };
    }[];
};
