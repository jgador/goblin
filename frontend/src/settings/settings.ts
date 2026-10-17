import { mountCodex } from "../connection/codex.js";
import { mountGitHub } from "./github.js";
import { mountSlack } from "./slack.js";
import { icon, escapeHtml as e } from "../work/presentation.js";
import { SystemResources } from "./system.js";
import { mountTimeZonePicker } from "./timezone-picker.js";
import { requestJson } from "../api/client.js";
import { ReadScope } from "../api/read-scope.js";
import type { Agent } from "../api/workspace-contracts.js";

const providers = [
    "agents",
    "connections",
    "codex",
    "github",
    "system",
    "cluster",
    "logs",
    "timezone",
    "integrations",
    "slack",
] as const;
type Provider = (typeof providers)[number];
function providerFrom(value: string): Provider {
    return providers.find((provider) => provider === value) ?? "connections";
}

export class Settings {
    readonly element = document.createElement("section");
    private codexMounted = false;
    private githubMounted = false;
    private slackDispose?: () => void;
    private generation = 0;
    private codexDispose?: () => void;
    private githubDispose?: () => void;
    private timezoneDispose?: () => void;
    private selected?: Provider;
    private readonly reads = new ReadScope();

    constructor(
        private readonly system: SystemResources,
        private readonly onNavigate: (provider: string) => void,
    ) {
        this.element.className = "settings-page";
        this.element.dataset.scroll = "settings";
        this.element.setAttribute("aria-labelledby", "settings-title");
        const groups = [
            {
                label: "Workspace",
                items: [
                    ["agents", "Agents", "agents"],
                    ["timezone", "Time & date", "clock"],
                ],
            },
            {
                label: "Connections",
                items: [
                    ["connections", "AI connections", "spark"],
                    ["github", "GitHub", "branch"],
                    ["integrations", "Integrations", "integrations"],
                ],
            },
            {
                label: "Administration",
                items: [
                    ["system", "System", "activity"],
                    ["cluster", "Cluster", "work"],
                    ["logs", "Logs", "file"],
                ],
            },
        ];
        this.element.innerHTML = `<header class="settings-page-heading"><div><p class="page-eyebrow">YOUR WORKSPACE</p><h1 id="settings-title" tabindex="-1">Settings</h1><p>Make Goblin feel at home.</p></div><button class="secondary" data-action="return-settings" type="button">${icon("arrow")}<span>Back to workspace</span></button></header><label class="settings-section-select">Settings section<select id="settings-section" aria-label="Settings section" class="field-control select-control">${groups.map((group) => `<optgroup label="${group.label}">${group.items.map(([provider, label]) => `<option value="${provider}">${label}</option>`).join("")}</optgroup>`).join("")}</select></label><div class="settings-layout"><nav class="settings-nav" aria-label="Settings sections">${groups.map((group) => `<div class="settings-nav-group"><p>${group.label}</p>${group.items.map(([provider, label, glyph]) => `<a href="/settings?section=${provider}" data-provider="${provider}">${icon(glyph!)}${label}</a>`).join("")}</div>`).join("")}<button class="settings-lock" data-action="lock">${icon("lock")}Lock workspace</button></nav><div class="settings-content">${providers.map((provider) => `<section data-provider-panel="${provider}" aria-label="${provider === "system" ? "System resources" : provider === "codex" ? "Codex connection" : provider === "github" ? "GitHub connection" : provider}" class="${provider === "codex" ? "codex-settings" : ""}" hidden></section>`).join("")}</div></div>`;
        this.element
            .querySelector<HTMLSelectElement>("#settings-section")!
            .addEventListener("change", (event) => {
                this.open((event.target as HTMLSelectElement).value);
            });
        this.element.addEventListener("click", (event) => {
            if (!(event.target instanceof Element)) return;
            const link = event.target.closest<HTMLElement>("[data-provider]");
            if (
                link &&
                !(
                    event.ctrlKey ||
                    event.metaKey ||
                    event.shiftKey ||
                    event.altKey
                )
            ) {
                event.preventDefault();
                this.open(link.dataset.provider);
            }
            if (event.target.closest("[data-open-system-cluster]"))
                this.open("cluster");
        });
    }
    reset() {
        this.generation++;
        this.reads.reset();
        this.codexDispose?.();
        this.githubDispose?.();
        this.slackDispose?.();
        this.timezoneDispose?.();
        this.codexDispose =
            this.githubDispose =
            this.slackDispose =
            this.timezoneDispose =
                undefined;
        this.codexMounted = this.githubMounted = false;
        this.selected = undefined;
        this.element.remove();
        for (const panel of this.element.querySelectorAll<HTMLElement>(
            "[data-provider-panel]",
        ))
            panel.replaceWith(panel.cloneNode(false));
    }
    open(value = "connections") {
        this.onNavigate(providerFrom(value));
    }
    attach(host: HTMLElement, value: string) {
        host.append(this.element);
        const provider = providerFrom(value);
        if (this.selected === provider) return;
        this.selected = provider;
        if (provider !== "slack") {
            this.slackDispose?.();
            this.slackDispose = undefined;
        }
        if (provider !== "timezone") {
            this.timezoneDispose?.();
            this.timezoneDispose = undefined;
        }
        this.element.scrollTop = 0;
        this.element
            .querySelectorAll<HTMLElement>("[data-provider-panel]")
            .forEach((panel) => {
                panel.hidden = panel.dataset.providerPanel !== provider;
            });
        const parent =
            provider === "codex"
                ? "connections"
                : provider === "slack"
                  ? "integrations"
                  : provider;
        this.element.querySelector<HTMLSelectElement>(
            "#settings-section",
        )!.value = parent;
        this.element
            .querySelectorAll<HTMLElement>("[data-provider]")
            .forEach((link) => {
                const active = link.dataset.provider === parent;
                link.classList.toggle("selected", active);
                if (active) link.setAttribute("aria-current", "page");
                else link.removeAttribute("aria-current");
            });
        void this.mount(provider);
    }
    private api<T>(path: string) {
        return requestJson<T>(path, {
            signal: this.reads.signal,
            onUnauthorized: () =>
                window.dispatchEvent(new Event("goblin-workspace-locked")),
        });
    }
    private async mount(provider: Provider) {
        if (provider === "integrations") {
            const panel = this.element.querySelector<HTMLElement>(
                '[data-provider-panel="integrations"]',
            )!;
            panel.innerHTML =
                '<h3>Integrations</h3><p class="settings-description">Connect and manage external tools in the integrations directory.</p><a class="settings-primary" href="/integrations" data-action="integrations" data-open-directory>Open integrations</a>';
            return;
        }
        if (provider === "slack") {
            this.slackDispose?.();
            this.slackDispose = mountSlack(
                this.element.querySelector<HTMLElement>(
                    '[data-provider-panel="slack"]',
                )!,
            );
            return;
        }
        if (provider === "agents") {
            const panel = this.element.querySelector<HTMLElement>(
                '[data-provider-panel="agents"]',
            )!;
            const generation = this.generation;
            panel.innerHTML =
                '<h3>Agents</h3><p class="settings-description">Loading agents…</p>';
            try {
                const agents = await this.api<Agent[]>("/api/agents");
                if (generation !== this.generation) return;
                panel.innerHTML = `<h3>Agents</h3><p class="settings-description">Your coworkers carry work from its goal through review.</p>${agents.map((agent) => `<div class="provider-card"><div class="provider-heading">${icon("user")}<h4>${e(agent.name)}</h4>${agent.isDefault ? '<span class="provider-default">Default</span>' : ""}</div><p class="settings-description">${agent.model ? e(agent.model) : "Uses the connected runtime’s default model"}</p></div>`).join("") || '<p class="settings-description">No agents are available yet.</p>'}`;
            } catch {
                if (generation !== this.generation) return;
                panel.innerHTML =
                    '<h3>Agents</h3><p class="settings-notice" role="alert">Agents could not be loaded. Open another section and try again.</p>';
            }
            return;
        }
        if (provider === "timezone") {
            this.timezoneDispose?.();
            this.timezoneDispose = mountTimeZonePicker(
                this.element.querySelector<HTMLElement>(
                    '[data-provider-panel="timezone"]',
                )!,
                "settings",
            );
            return;
        }
        if (provider === "system") {
            this.system.mount(
                this.element.querySelector<HTMLElement>(
                    '[data-provider-panel="system"]',
                )!,
            );
            return;
        }
        if (provider === "connections") {
            const panel = this.element.querySelector<HTMLElement>(
                '[data-provider-panel="connections"]',
            )!;
            panel.innerHTML = `<h3>AI connections</h3><p class="settings-description">Choose how Goblin connects to the AI that does your work.</p><div class="provider-card"><div class="provider-heading">${icon("spark")}<h4>Codex</h4><span class="provider-default">Default</span></div><p class="settings-description">Connect with your ChatGPT account or an OpenAI API key.</p><button class="settings-primary" data-connect-codex>Manage connection</button></div><p class="settings-description">GitHub repository access is managed separately under GitHub.</p>`;
            panel
                .querySelector("[data-connect-codex]")!
                .addEventListener("click", () => {
                    this.open("codex");
                });
            return;
        }
        if (provider === "cluster") {
            const generation = this.generation;
            const panel = this.element.querySelector<HTMLElement>(
                '[data-provider-panel="cluster"]',
            )!;
            panel.innerHTML =
                '<h3>Cluster</h3><p class="settings-description">Loading cluster access…</p>';
            try {
                const cluster = await this.api<{ available: boolean }>(
                    "/api/cluster",
                );
                if (generation !== this.generation) return;
                panel.innerHTML = `<h3>Cluster</h3><p class="settings-description">View pods, logs, events, and storage in Headlamp.</p>${
                    cluster.available
                        ? '<p class="settings-description">Your Goblin login gives you read-only access. Opens in a new tab.</p><a class="settings-primary" href="/headlamp/" target="_blank" rel="noopener">Open cluster</a>'
                        : '<p class="settings-notice">The cluster view is available with Goblin’s Kubernetes installation.</p>'
                }`;
            } catch {
                if (generation !== this.generation) return;
                panel.innerHTML =
                    '<h3>Cluster</h3><p class="settings-notice">Cluster access could not be loaded. Unlock Goblin and try again.</p>';
            }
            return;
        }
        if (provider === "logs") {
            const panel = this.element.querySelector<HTMLElement>(
                '[data-provider-panel="logs"]',
            )!;
            const generation = this.generation;
            panel.innerHTML =
                '<h3>Logs</h3><p class="settings-description">Loading log access…</p>';
            try {
                const logs = await this.api<{ available: boolean }>(
                    "/api/logs",
                );
                if (generation !== this.generation) return;
                panel.innerHTML =
                    '<h3>Logs</h3><p class="settings-description">Search saved Goblin logs and follow new events.</p>' +
                    (logs.available
                        ? '<p class="settings-description">Opens in a new tab using your Goblin login.</p><a class="settings-primary" href="/logs/" target="_blank" rel="noopener">Open logs</a>'
                        : '<p class="settings-notice">Saved logs are available with Goblin’s Kubernetes installation.</p>');
            } catch {
                if (generation !== this.generation) return;
                panel.innerHTML =
                    '<h3>Logs</h3><p class="settings-notice">Log access could not be loaded. Unlock Goblin and try again.</p>';
            }
            return;
        }
        if (provider === "github") {
            if (!this.githubMounted) {
                this.githubMounted = true;
                this.githubDispose = mountGitHub(
                    this.element.querySelector<HTMLElement>(
                        '[data-provider-panel="github"]',
                    )!,
                );
            }
            return;
        }
        if (this.codexMounted) return;
        this.codexMounted = true;
        const panel = this.element.querySelector<HTMLElement>(
            '[data-provider-panel="codex"]',
        )!;
        const generation = this.generation;
        try {
            const response = await fetch("/connection/panel.html", {
                signal: this.reads.signal,
            });
            if (!response.ok) throw new Error();
            const html = await response.text();
            if (generation !== this.generation) return;
            panel.innerHTML =
                '<button class="provider-back" type="button">← AI connections</button>' +
                html;
            panel
                .querySelector(".provider-back")!
                .addEventListener("click", () => {
                    this.open("connections");
                });
            const dispose = await mountCodex(
                panel,
                undefined,
                this.reads.signal,
            );
            if (generation !== this.generation) dispose?.();
            else this.codexDispose = dispose;
        } catch {
            if (generation !== this.generation) return;
            panel.innerHTML =
                '<p class="settings-notice" role="alert">Codex settings could not be loaded. Return to AI connections and try again.</p>';
            this.codexMounted = false;
        }
    }
}
