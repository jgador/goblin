export type ChatGptState = {
    configured: boolean;
    name: string;
    description: string;
    logoUrl: string;
    serverUrl: string | null;
    issuer: string | null;
    clientId: string;
    scope: string;
    ownerSubject: string | null;
    clientConfigured: boolean;
    redirectUri: string | null;
    environmentVariables: { publicOrigin: string; enabled: string };
    grants: {
        id: string;
        subject: string;
        workId: string;
        resultAttemptId: string | null;
        expiresAt: string;
        expired: boolean;
    }[];
};

export type ChatGptVerification = {
    discovery: boolean;
    database: boolean;
    issuerResponses: boolean;
    checkedAt: string;
};

export type ChatGptPackage = {
    pluginJson: string;
    mcpJson: string;
    archiveBase64: string;
};

export type PluginCreatorAccess = {
    runtimeSupported: boolean;
    skillAvailable: boolean;
    skillSha256: string | null;
    appInstalled: boolean;
    appEnabled: boolean;
    appCallable: boolean;
    tools: { name: string; enabled: boolean; readOnly: boolean }[];
    checkedAt: string;
    automaticCreationAvailable: boolean;
};

export type ChatGptCreatorDraft = {
    description: string;
    creatorPrompt: string;
    model: string;
    durationMs: number;
    creatorAccess: PluginCreatorAccess;
    archiveBase64: string;
    pluginRegistered: boolean;
    connectionVerified: boolean;
};
