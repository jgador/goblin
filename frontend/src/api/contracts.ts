// Shared HTTP contracts. API keys and configured passwords have no response fields.
export type Account =
  | { type: "chatgpt"; email: string | null; planType: string | null }
  | { type: "apiKey" };

export type Verification = "accepted" | "unverified";
export type Notice = { kind: "error" | "info"; message: string };
export type DeviceLogin = { id: string; verificationUrl: string; userCode: string };

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
  authType: Account["type"];
}

export interface SessionState {
  authenticated: boolean;
  /** Public convenience password for a server bound exclusively to loopback. */
  localDefaultPassword?: string;
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
