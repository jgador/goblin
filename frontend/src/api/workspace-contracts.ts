import type { ConnectionAvailability } from "./values.js";

export type Agent = {
    id: string;
    name: string;
    connectionId: string;
    model?: string;
    isDefault: boolean;
};

export type Connection = {
    id: string;
    runtime: string;
    name: string;
    availability: ConnectionAvailability;
};

export type RuntimeCapabilities = {
    runtime: string;
    repositoryExecution: boolean;
};
