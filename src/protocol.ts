// The subset of the pinned Codex app-server protocol used by this preview.
// These types describe the wire format; callers still validate external data.
import type { Account } from "../shared/api.js";

export interface CodexRequests {
  initialize: { clientInfo: { name: string; title: string; version: string } };
  "account/read": { refreshToken: boolean };
  "account/login/start": { type: "chatgptDeviceCode" } | { type: "apiKey"; apiKey: string };
  "account/login/cancel": { loginId: string };
  "account/logout": undefined;
  "thread/start": {
    cwd: string;
    ephemeral: boolean;
    approvalPolicy: "never";
    sandbox: "read-only";
    baseInstructions: string;
  };
  "turn/start": {
    threadId: string;
    input: Array<{ type: "text"; text: string }>;
    approvalPolicy: "never";
    sandboxPolicy: { type: "readOnly"; networkAccess: boolean };
  };
  "turn/interrupt": { threadId: string; turnId: string };
  "thread/unsubscribe": { threadId: string };
}

export interface CodexResponses {
  initialize: { userAgent: string };
  "account/read": { account: Account | null; requiresOpenaiAuth: boolean };
  "account/login/start":
    | { type: "apiKey" }
    | { type: "chatgptDeviceCode"; loginId: string; verificationUrl: string; userCode: string };
  "account/login/cancel": unknown;
  "account/logout": unknown;
  "thread/start": {
    thread: { id: string; ephemeral: boolean };
    model: string;
    modelProvider: string;
    sandbox: { type: string };
    approvalPolicy: string;
  };
  "turn/start": { turn: { id: string } };
  "turn/interrupt": unknown;
  "thread/unsubscribe": unknown;
}

export interface CodexNotification {
  method: string;
  params: Record<string, unknown>;
}

export type CodexEvents = {
  notification: [message: CodexNotification];
  disconnected: [];
};
