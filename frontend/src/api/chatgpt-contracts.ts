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
};

export type ChatGptVerification = {
    discovery: boolean;
    database: boolean;
    issuerResponses: boolean;
    checkedAt: string;
};
