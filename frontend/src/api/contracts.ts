import type {
    AuthenticationMethod,
    NoticeKind,
    VerificationState,
} from "./values.js";
// Shared HTTP contracts. API keys and configured passwords have no response fields.
export type Account =
    | {
          type: typeof import("./values.js").AuthenticationMethod.Chatgpt;
          email: string | null;
          planType: string | null;
      }
    | { type: typeof import("./values.js").AuthenticationMethod.ApiKey };

export type Verification = VerificationState;
export type Notice = { kind: NoticeKind; message: string };
export type DeviceLogin = {
    id: string;
    verificationUrl: string;
    userCode: string;
};

export interface AuthenticationState {
    account: Account | null;
    login: DeviceLogin | null;
    notice: Notice | null;
    verification: Verification | null;
    runtimeReady: boolean;
}

export interface PromptResult {
    reply: string;
    model: string;
    durationMs: number;
    authType: AuthenticationMethod;
}

export interface SessionState {
    authenticated: boolean;
}

export interface ApiFailure {
    error: { code: string; message: string };
}

export interface ApiResponses {
    "/api/session": SessionState;
    "/api/session/lock": SessionState;
    "/api/status": AuthenticationState;
    "/api/auth/chatgpt": AuthenticationState;
    "/api/auth/api-key": AuthenticationState;
    "/api/auth/cancel": AuthenticationState;
    "/api/auth/logout": AuthenticationState;
    "/api/prompt": PromptResult;
}

export interface ApiRequestBodies {
    "/api/session": { password: string };
    "/api/session/lock": Record<string, never>;
    "/api/status": undefined;
    "/api/auth/chatgpt": Record<string, never>;
    "/api/auth/api-key": { apiKey: string };
    "/api/auth/cancel": Record<string, never>;
    "/api/auth/logout": Record<string, never>;
    "/api/prompt": { prompt: string };
}
