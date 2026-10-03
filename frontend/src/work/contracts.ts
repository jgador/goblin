import type {
    WorkStatus,
    AttemptStatus,
    AttentionReason,
    FailureKind,
    WorkEventKind,
    GitRepositoryAuthorizationStatus,
} from "../api/values.js";
// Goblin-owned Work HTTP views. IDs remain decimal strings.
// Property names follow the existing JSON contract, just as C# uses JsonPropertyName.
export type GitRepositoryGrant = {
    login: string;
    branch: string;
    baseBranch: string;
    allowPush: boolean;
    allowPullRequest: boolean;
};
export type GitRepositoryChange = {
    repository: string;
    gitAuthorName?: string;
    gitAuthorEmail?: string;
    grant?: GitRepositoryGrant;
};
export type ExecutionTarget = {
    runtime: string;
    requestedModel?: string;
    requestedEffort?: string;
    repository?: GitRepositoryChange;
};
export type WorkGitRepositoryRequest = {
    repositories: string[];
    target: ExecutionTarget;
};
export type GitRepositoryAuthorization = {
    id: string;
    status: GitRepositoryAuthorizationStatus;
    target: ExecutionTarget;
    enableRepository: boolean;
    retry: boolean;
};
export type Attempt = {
    id: string;
    status: AttemptStatus;
    agentId?: string;
    queuedAt?: string;
    startedAt?: string;
    finishedAt?: string;
    target: ExecutionTarget;
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
    repositoryRequest?: WorkGitRepositoryRequest;
    repositoryAuthorization?: GitRepositoryAuthorization;
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
