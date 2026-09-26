// Goblin-owned Work HTTP views. IDs remain decimal strings.
export type Attempt = {
    id: string;
    status: string;
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
            grant?: { login: string; branch: string };
        };
    };
    session?: { model?: string };
    failure?: string;
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
    status: string;
    agentId?: string;
    attention?: { reason: string; failure?: string };
    repositoryRequest?: {
        repositories: string[];
        target: Attempt["target"];
    };
    attempts: Attempt[];
    history: {
        sequence: string;
        kind: string;
        text?: string;
        failure?: string;
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
    messages: { id: string; text: string; createdAt: string }[];
};
