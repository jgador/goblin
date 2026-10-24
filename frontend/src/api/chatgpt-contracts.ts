export type ChatGptState = {
    configured: boolean;
    name: string;
    description: string;
    logoUrl: string;
    serverUrl: string | null;
    issuer: string | null;
    clientId: string;
    scope: string;
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
