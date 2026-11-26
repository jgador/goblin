import { requestJson } from "../api/client.js";
import { ReadScope } from "../api/read-scope.js";
import type { GitHubState, SlackState } from "../api/integration-contracts.js";
import type { ChatGptState } from "../api/chatgpt-contracts.js";
import {
    GitHubConnectionStatus,
    SlackConnectionStatus,
    SlackSetupStatus,
    ConnectionAvailability,
} from "../api/values.js";
import { icon, escapeHtml as e } from "../work/presentation.js";

import type { Connection } from "../api/workspace-contracts.js";
import { settingsHref } from "./navigation.js";
import type { ConnectionProvider } from "./navigation.js";
type Summary = {
    status: string;
    tone: "neutral" | "connected" | "attention";
    detail: string;
    action: "Connect" | "Manage" | "View setup";
    failed?: boolean;
};
const providers = [
    {
        id: "codex",
        name: "Codex",
        description: "Run Work with your ChatGPT account or an OpenAI API key.",
    },
    {
        id: "github",
        name: "GitHub",
        description: "Repositories, issues, and pull requests.",
    },
    {
        id: "slack",
        name: "Slack",
        description: "Start and continue Work from DMs and mentions.",
    },
    {
        id: "chatgpt",
        name: "ChatGPT",
        description:
            "Read and manage all Work through your private ChatGPT plugin.",
    },
    {
        id: "teams",
        name: "Microsoft Teams",
        description: "Bring Work into your team’s conversations.",
    },
] as const;

const loading = (): Summary => ({
    status: "Checking…",
    tone: "neutral",
    detail: "Loading connection status.",
    action: "Manage",
});
const unknown = (): Summary => ({
    status: "Status unavailable",
    tone: "attention",
    detail: "Connection status could not be checked.",
    action: "Manage",
    failed: true,
});

function githubSummary(state: GitHubState): Summary {
    const account = state.login ? `Signed in as @${state.login}` : "";
    switch (state.status) {
        case GitHubConnectionStatus.Connected:
            return {
                status: "Connected",
                tone: "connected",
                detail: account,
                action: "Manage",
            };
        case GitHubConnectionStatus.Connecting:
            return {
                status: "Awaiting sign-in",
                tone: "attention",
                detail: "Finish authorizing your GitHub account.",
                action: "Manage",
            };
        case GitHubConnectionStatus.Unavailable:
            return {
                status: "Needs attention",
                tone: "attention",
                detail: "Review your GitHub connection in settings.",
                action: "Manage",
            };
        case GitHubConnectionStatus.Disconnected:
            return {
                status: "Not connected",
                tone: "neutral",
                detail: "Choose the repositories Goblin can access.",
                action: "Connect",
            };
        default:
            return unknown();
    }
}

function slackSummary(state: SlackState): Summary {
    if (!state.available)
        return {
            status: "Unavailable",
            tone: "neutral",
            detail: "Slack isn’t enabled in this workspace.",
            action: "View setup",
        };
    const { connection, setup } = state;
    if (connection.status === SlackConnectionStatus.Connected)
        return {
            status: "Connected",
            tone: "connected",
            detail: connection.workspace ?? "Slack workspace",
            action: "Manage",
        };
    if (
        setup.status === SlackSetupStatus.NeedsAttention ||
        connection.status === SlackConnectionStatus.Unavailable
    )
        return {
            status: "Needs attention",
            tone: "attention",
            detail: "Review your Slack connection in settings.",
            action: "Manage",
        };
    if (setup.status === SlackSetupStatus.AwaitingAuthorization)
        return {
            status: "Awaiting authorization",
            tone: "attention",
            detail: "Finish authorizing your Slack app.",
            action: "Manage",
        };
    if (
        connection.status === SlackConnectionStatus.Connecting ||
        setup.status === SlackSetupStatus.Preparing ||
        setup.status === SlackSetupStatus.Installing
    )
        return {
            status: "Connecting",
            tone: "neutral",
            detail: "Your Slack connection is being set up.",
            action: "Manage",
        };
    if (connection.status === SlackConnectionStatus.Disconnected)
        return {
            status: "Not connected",
            tone: "neutral",
            detail: "Connect an app owned by your workspace.",
            action: "Connect",
        };
    return unknown();
}

function providerLogo(provider: (typeof providers)[number]["id"]) {
    return (
        '<span class="connection-logo"><img src="/assets/providers/' +
        provider +
        (provider === "codex" ? ".png" : ".svg") +
        '" width="24" height="24" alt=""></span>'
    );
}

function chatgptSummary(state: ChatGptState): Summary {
    // Configuration alone cannot attest to installation or OAuth in ChatGPT.
    return {
        status: state.configured ? "Configured" : "Not configured",
        tone: "neutral",
        detail: state.configured
            ? "Verify full Work access and sign-in in ChatGPT."
            : "Connect a plugin to your own Goblin instance.",
        action: "View setup",
    };
}

function codexSummary(connections: Connection[]): Summary {
    const connection = connections.find((value) => value.runtime === "codex");
    if (
        !connection ||
        connection.availability === ConnectionAvailability.Disconnected
    )
        return {
            status: "Not connected",
            tone: "neutral",
            detail: "",
            action: "Connect",
        };
    if (connection.availability === ConnectionAvailability.Available)
        return {
            status: "Connected",
            tone: "connected",
            detail: "Ready to run Work.",
            action: "Manage",
        };
    if (
        connection.availability === ConnectionAvailability.Verifying ||
        connection.availability === ConnectionAvailability.Changing
    )
        return {
            status:
                connection.availability === ConnectionAvailability.Verifying
                    ? "Verifying sign-in"
                    : "Changing connection",
            tone: "neutral",
            detail: "",
            action: "Manage",
        };
    return {
        status: "Needs attention",
        tone: "attention",
        detail: "Review your AI connection.",
        action: "Manage",
    };
}

export class Connections {
    readonly element = document.createElement("div");
    private readonly reads = new ReadScope();
    private states: Record<ConnectionProvider, Summary> = {
        codex: loading(),
        github: loading(),
        slack: loading(),
        chatgpt: loading(),
    };
    private loading = false;
    private loaded = false;

    constructor() {
        this.element.className = "connections-overview";
        this.render();
        this.element.addEventListener("click", (event) => {
            if (
                event.target instanceof Element &&
                event.target.closest("[data-refresh-connections]")
            )
                void this.refresh();
        });
    }

    deactivate() {
        this.reads.reset();
        this.loading = false;
    }

    reset() {
        this.deactivate();
        this.states = {
            codex: loading(),
            github: loading(),
            slack: loading(),
            chatgpt: loading(),
        };
        this.loaded = false;
        this.render();
    }

    async refresh() {
        if (this.loading) return;
        this.loading = true;
        const signal = this.reads.signal;
        const options = {
            signal,
            onUnauthorized: () =>
                window.dispatchEvent(new Event("goblin-workspace-locked")),
        };
        const results = await Promise.allSettled([
            requestJson<Connection[]>("/api/connections", options).then(
                codexSummary,
            ),
            requestJson<GitHubState>("/api/github", options).then(
                githubSummary,
            ),
            requestJson<SlackState>("/api/integrations/slack", options).then(
                slackSummary,
            ),
            requestJson<ChatGptState>(
                "/api/integrations/chatgpt",
                options,
            ).then(chatgptSummary),
        ]);
        if (signal.aborted) return;
        const before = JSON.stringify(this.states);
        const [codex, github, slack, chatgpt] = results;
        this.states = {
            codex: codex.status === "fulfilled" ? codex.value : unknown(),
            github: github.status === "fulfilled" ? github.value : unknown(),
            slack: slack.status === "fulfilled" ? slack.value : unknown(),
            chatgpt: chatgpt.status === "fulfilled" ? chatgpt.value : unknown(),
        };
        this.loading = false;
        const firstLoad = !this.loaded;
        this.loaded = true;
        if (firstLoad || JSON.stringify(this.states) !== before) this.render();
    }

    private render() {
        const focusId = this.element.contains(document.activeElement)
            ? document.activeElement?.id
            : undefined;
        const failed = Object.values(this.states).some(
            (summary) => summary.failed,
        );
        const [runtime, ...integrations] = providers;
        const row = (provider: (typeof providers)[number]) => {
            const planned = provider.id === "teams";
            const summary = planned ? undefined : this.states[provider.id];
            const action =
                summary && provider.id !== "teams"
                    ? `<a id="connection-${provider.id}" class="connection-action" href="${settingsHref(provider.id)}" data-provider="${provider.id}" aria-label="${summary.action} ${provider.name}">${summary.action}${icon("chevron")}</a>`
                    : "";
            return `<li class="connection-row" data-connection="${provider.id}">
                ${providerLogo(provider.id)}
                <div class="connection-copy"><h4>${provider.name}</h4><p>${provider.description}</p></div>
                <div class="connection-meta"><span class="connection-status ${summary?.tone ?? "neutral"}">${e(summary?.status ?? "Planned")}</span>${summary?.detail ? `<span class="connection-detail">${e(summary.detail)}</span>` : ""}</div>
                ${action}
            </li>`;
        };
        this.element.innerHTML = `<h2 id="connections-title" tabindex="-1">Connections</h2>
            ${failed ? '<p class="settings-notice" role="status">Some connection statuses are unavailable. <button id="retry-connections" class="text-button" data-refresh-connections>Try again</button></p>' : ""}
            <section class="connection-group" aria-labelledby="connection-runtime-title">
                <h3 id="connection-runtime-title">Agent runtime</h3>
                <ul class="connection-list" role="list" aria-labelledby="connection-runtime-title" aria-busy="${!this.loaded}">${row(runtime)}</ul>
            </section>
            <section class="connection-group" aria-labelledby="connection-integrations-title">
                <h3 id="connection-integrations-title">Integrations</h3>
                <ul class="connection-list" role="list" aria-labelledby="connection-integrations-title" aria-busy="${!this.loaded}">${integrations.map(row).join("")}</ul>
            </section>`;
        if (focusId)
            this.element.querySelector<HTMLElement>("#" + focusId)?.focus();
    }
}
