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
