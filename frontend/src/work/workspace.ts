import { requestJson } from "../api/client.js";
import { ReadScope } from "../api/read-scope.js";
import { ConnectionAvailability } from "../api/values.js";
import type {
    Agent,
    Connection,
    RuntimeCapabilities,
} from "../api/workspace-contracts.js";
import type { GitRepositoryInfo } from "../settings/github.js";
import type { Conversation, View } from "./contracts.js";

type WorkspaceState = {
    work: readonly View[];
    conversations: readonly Conversation[];
    agents: readonly Agent[];
    runtimes: readonly RuntimeCapabilities[];
    connections: readonly Connection[];
    repositories: readonly GitRepositoryInfo[];
    loaded: boolean;
    workUnavailable: boolean;
    connectionsLoaded: boolean;
};

const emptyWorkspace: Readonly<WorkspaceState> = {
    work: [],
    conversations: [],
    agents: [],
    runtimes: [],
    connections: [],
    repositories: [],
    loaded: false,
    workUnavailable: false,
    connectionsLoaded: false,
};

export class WorkspaceController {
    private readonly reads = new ReadScope();
    private snapshot = emptyWorkspace;
    private checkingConnections = false;

    constructor(private readonly onUnauthorized: () => void) {}

    get state() {
        return this.snapshot;
    }

    get signal() {
        return this.reads.signal;
    }

    reset() {
        this.reads.reset();
        this.snapshot = emptyWorkspace;
        this.checkingConnections = false;
    }

    request<T>(path: string, body?: unknown): Promise<T> {
        const session = this.signal;
        return requestJson<T>(path, {
            body,
            signal: body === undefined ? session : undefined,
            onUnauthorized: () => {
                if (!session.aborted) this.onUnauthorized();
            },
        });
    }

    async refreshWork() {
        const session = this.signal;
        const [work, conversations, agents, runtimes] =
            await Promise.allSettled([
                this.request<View[]>("/api/work"),
                this.request<Conversation[]>("/api/conversations"),
                this.request<Agent[]>("/api/agents"),
                this.request<RuntimeCapabilities[]>("/api/runtimes"),
            ]);
        session.throwIfAborted();
        const previous = this.snapshot;
        const loaded = work.status === "fulfilled";
        this.snapshot = {
            ...previous,
            work: loaded ? work.value : previous.work,
            conversations:
                conversations.status === "fulfilled"
                    ? conversations.value
                    : previous.conversations,
            agents:
                agents.status === "fulfilled" ? agents.value : previous.agents,
            runtimes:
                runtimes.status === "fulfilled"
                    ? runtimes.value
                    : previous.runtimes,
            loaded: loaded || previous.loaded,
            workUnavailable: !loaded,
        };
        return loaded;
    }

    async refreshConnections(): Promise<string[] | undefined> {
        if (this.checkingConnections) return;
        this.checkingConnections = true;
        const session = this.signal;
        const [runtime, repositories] = await Promise.allSettled([
            this.request<Connection[]>("/api/connections"),
            this.request<GitRepositoryInfo[]>("/api/github/repositories"),
        ]);
        if (session.aborted) return;
        const previous = this.snapshot;
        const connections =
            runtime.status === "fulfilled"
                ? runtime.value
                : previous.connections.map((connection) => ({
                      ...connection,
                      availability: ConnectionAvailability.Unavailable,
                  }));
        this.snapshot = {
            ...previous,
            connections,
            repositories:
                repositories.status === "fulfilled"
                    ? repositories.value
                    : previous.repositories,
            connectionsLoaded: true,
        };
        this.checkingConnections = false;
        const alreadyAvailable = new Set(
            previous.connections
                .filter(
                    (connection) =>
                        connection.availability ===
                        ConnectionAvailability.Available,
                )
                .map((connection) => connection.id),
        );
        return connections
            .filter(
                (connection) =>
                    connection.availability ===
                        ConnectionAvailability.Available &&
                    !alreadyAvailable.has(connection.id),
            )
            .map((connection) => connection.id);
    }
}
