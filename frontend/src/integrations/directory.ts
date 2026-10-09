import { requestJson } from "../api/client.js";
import { ReadScope } from "../api/read-scope.js";
import type { GitHubState, SlackState } from "../api/integration-contracts.js";
import {
    GitHubConnectionStatus,
    SlackConnectionStatus,
    SlackSetupStatus,
} from "../api/values.js";
import { icon, escapeHtml as e } from "../work/presentation.js";

type Provider = "github" | "slack";
type Summary = {
    status: string;
    tone: "neutral" | "connected" | "attention";
    detail: string;
    action: "Connect" | "Manage" | "View setup";
    connected: boolean;
    failed?: boolean;
};
const providers = [
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
    connected: false,
});
const unknown = (): Summary => ({
    status: "Status unavailable",
    tone: "attention",
    detail: "Connection status could not be checked.",
    action: "Manage",
    connected: false,
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
                connected: true,
            };
        case GitHubConnectionStatus.Connecting:
            return {
                status: "Awaiting sign-in",
                tone: "attention",
                detail: "Finish authorizing your GitHub account.",
                action: "Manage",
                connected: false,
            };
        case GitHubConnectionStatus.Unavailable:
            return {
                status: "Needs attention",
                tone: "attention",
                detail: "Review your GitHub connection in settings.",
                action: "Manage",
                connected: false,
            };
        case GitHubConnectionStatus.Disconnected:
            return {
                status: "Not connected",
                tone: "neutral",
                detail: "Choose the repositories Goblin can access.",
                action: "Connect",
                connected: false,
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
            connected: false,
        };
    const { connection, setup } = state;
    if (connection.status === SlackConnectionStatus.Connected)
        return {
            status: "Connected",
            tone: "connected",
            detail: connection.workspace ?? "Slack workspace",
            action: "Manage",
            connected: true,
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
            connected: false,
        };
    if (setup.status === SlackSetupStatus.AwaitingAuthorization)
        return {
            status: "Awaiting authorization",
            tone: "attention",
            detail: "Finish authorizing your Slack app.",
            action: "Manage",
            connected: false,
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
            connected: false,
        };
    if (connection.status === SlackConnectionStatus.Disconnected)
        return {
            status: "Not connected",
            tone: "neutral",
            detail: "Connect an app owned by your workspace.",
            action: "Connect",
            connected: false,
        };
    return unknown();
}

export function providerLogo(provider: (typeof providers)[number]["id"]) {
    return `<img class="provider-logo" src="/assets/providers/${provider}.svg" width="36" height="36" alt="">`;
}

export class IntegrationsDirectory {
    private readonly reads = new ReadScope();
    private readonly changed: () => void;
    private readonly manage: (provider: Provider) => void;
    private readonly picker = document.createElement("dialog");
    private states: Record<Provider, Summary> = {
        github: loading(),
        slack: loading(),
    };
    private loading = false;
    private loaded = false;
    search = "";
    tab: "all" | "connected" = "all";

    constructor(changed: () => void, manage: (provider: Provider) => void) {
        this.changed = changed;
        this.manage = manage;
        this.picker.className = "integration-picker";
        this.picker.setAttribute("aria-labelledby", "integration-picker-title");
        document.body.append(this.picker);
        this.picker.addEventListener("click", (event) => {
            if (!(event.target instanceof Element)) return;
            if (event.target.closest("[data-close-picker]"))
                this.picker.close();
            const provider = event.target.closest<HTMLElement>(
                "[data-pick-provider]",
            )?.dataset.pickProvider;
            if (provider === "github" || provider === "slack") {
                this.picker.close();
                document.getElementById("add-integration")?.focus();
                this.manage(provider);
            }
        });
        this.picker.addEventListener("close", () => {
            if (!document.querySelector("dialog[open]"))
                document.getElementById("add-integration")?.focus();
        });
    }

    get isPickerOpen() {
        return this.picker.open;
    }

    reset() {
        this.reads.reset();
        this.picker.close();
        this.picker.replaceChildren();
        this.states = { github: loading(), slack: loading() };
        this.loading = this.loaded = false;
        this.search = "";
        this.tab = "all";
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
            requestJson<GitHubState>("/api/github", options).then(
                githubSummary,
            ),
            requestJson<SlackState>("/api/integrations/slack", options).then(
                slackSummary,
            ),
        ]);
        if (signal.aborted) return;
        const before = JSON.stringify(this.states);
        const [github, slack] = results;
        this.states = {
            github: github.status === "fulfilled" ? github.value : unknown(),
            slack: slack.status === "fulfilled" ? slack.value : unknown(),
        };
        this.loading = false;
        this.loaded = true;
        if (JSON.stringify(this.states) !== before) this.changed();
    }

    openPicker() {
        this.picker.innerHTML = `<header><h2 id="integration-picker-title">Add integration</h2><button class="icon-button" data-close-picker aria-label="Close integration picker">${icon("close")}</button></header><p>Choose a tool to connect or manage.</p><div class="integration-choices">${providers
            .filter((p) => p.id !== "teams")
            .map(
                (p) =>
                    `<button data-pick-provider="${p.id}">${providerLogo(p.id)}<span><strong>${p.name}</strong><small>${p.description}</small></span>${icon("chevron")}</button>`,
            )
            .join(
                "",
            )}</div><p class="integration-picker-note">You’ll review authentication and access in connection settings.</p>`;
        this.picker.showModal();
    }

    render() {
        const connected = Object.values(this.states).filter(
            (s) => s.connected,
        ).length;
        const failed = Object.values(this.states).some((s) => s.failed);
        const query = this.search.trim().toLocaleLowerCase();
        const shown = providers.filter(
            (p) =>
                `${p.name} ${p.description}`
                    .toLocaleLowerCase()
                    .includes(query) &&
                (this.tab === "all" ||
                    (p.id !== "teams" && this.states[p.id].connected)),
        );
        return `<section class="directory-page" aria-labelledby="integrations-title" data-scroll="integrations">
            <header class="directory-heading"><div><h1 id="integrations-title" tabindex="-1">Integrations</h1><p>Connect Goblin to the tools your team already uses.</p></div><button id="add-integration" class="primary" data-action="add-integration">${icon("plus")}Add integration</button></header>
            <div class="directory-toolbar"><div class="directory-tabs" role="tablist" aria-label="Integration connections">${(["all", "connected"] as const).map((tab) => `<button id="integrations-tab-${tab}" role="tab" aria-selected="${this.tab === tab}" aria-controls="integration-results" tabindex="${this.tab === tab ? 0 : -1}" data-action="integration-tab" data-value="${tab}">${tab === "all" ? "All" : `Connected${this.loaded && !failed ? ` <span>${connected}</span>` : ""}`}</button>`).join("")}</div><label class="directory-search">${icon("search")}<input id="integration-search" type="search" placeholder="Search integrations…" aria-label="Search integrations" value="${e(this.search)}" autocomplete="off"></label></div>
            ${failed ? `<p class="directory-notice" role="status">Some connection statuses are unavailable. <button data-action="refresh-integrations">Try again</button></p>` : ""}
            <div id="integration-results" role="tabpanel" aria-labelledby="integrations-tab-${this.tab}" tabindex="0" aria-busy="${!this.loaded}"><p class="sr-only" role="status">${!this.loaded ? "Checking connection statuses." : `${shown.length} integration${shown.length === 1 ? "" : "s"} shown.`}</p>
            ${
                shown.length
                    ? `<ul class="integration-list">${shown
                          .map((p) => {
                              const summary: Summary =
                                  p.id === "teams"
                                      ? {
                                            status: "Planned",
                                            tone: "neutral",
                                            detail: "Not available to connect yet.",
                                            action: "Connect",
                                            connected: false,
                                        }
                                      : this.states[p.id];
                              return `<li class="integration-row" data-integration="${p.id}">${providerLogo(p.id)}<div class="integration-copy"><h2>${p.name}</h2><p>${p.description}</p><div class="integration-meta"><span class="integration-status ${summary.tone}">${summary.connected ? icon("check") : summary.tone === "attention" ? icon("wait") : ""}${e(summary.status)}</span>${summary.detail ? `<span class="integration-detail">${e(summary.detail)}</span>` : ""}</div></div>${p.id === "teams" ? "" : `<button id="manage-integration-${p.id}" class="secondary" data-action="manage-integration" data-provider="${p.id}" aria-label="${summary.action} ${p.name}">${summary.action}</button>`}</li>`;
                          })
                          .join("")}</ul>`
                    : `<div class="directory-empty">${icon(query ? "search" : "integrations")}<h2>${query ? "No matching integrations" : !this.loaded ? "Checking your connections…" : failed ? "Connections could not be verified" : "No connected integrations yet"}</h2><p>${query ? "Try another name or clear your search." : failed ? "Refresh the connection statuses or open All to manage a connection." : "Connect GitHub or Slack to bring your tools into Goblin."}</p><button class="secondary" data-action="reset-integrations">${query ? "Clear search" : "Browse integrations"}</button></div>`
            }
            </div><p class="directory-footnote">You control which repositories and Slack identities Goblin can use. Manage access in each integration’s settings.</p>
        </section>`;
    }
}
