import type {
    GitHubConnectionStatus,
    SlackConnectionStatus,
    SlackSetupStatus,
} from "./values.js";

export type GitHubState = {
    configured: boolean;
    login?: string;
    userCode?: string;
    verificationUrl?: string;
    notice?: string;
    status: GitHubConnectionStatus;
};

export type SlackState =
    | { available: false; connection: null; setup: null }
    | {
          available: true;
          connection: {
              status: SlackConnectionStatus;
              workspace?: string;
              workspaceId?: string;
              appId?: string;
              botUserId?: string;
              notice?: string;
          };
          setup: {
              status: SlackSetupStatus;
              command?: string;
              expiresAt?: string;
              appId?: string;
              notice?: string;
          };
          identities: { id: string; userId: string }[];
          link?: { id: string; userId?: string; expiresAt: string };
      };
