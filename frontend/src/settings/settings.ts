import { mountCodex } from "../connection/codex.js";
import { mountGitHub } from "./github.js";
import { mountSlack } from "./slack.js";
import { mountChatGpt } from "./chatgpt.js";
import { icon, escapeHtml as e } from "../work/presentation.js";
import { SystemResources } from "./system.js";
import { mountTimeZonePicker } from "./timezone-picker.js";
import { requestJson } from "../api/client.js";
import { ReadScope } from "../api/read-scope.js";
import type { Agent } from "../api/workspace-contracts.js";
import { Connections } from "./connections.js";
import {
    isConnectionProvider,
    settingsGroups,
    settingsHref,
    settingsPanels,
    settingsTarget,
} from "./navigation.js";
import type { SettingsSelection } from "./navigation.js";

export class Settings {
    readonly element = document.createElement("section");
    private codexMounted = false;
    private githubMounted = false;
    private slackDispose?: () => void;
    private chatgptDispose?: () => void;
    private generation = 0;
    private codexDispose?: () => void;
    private githubDispose?: () => void;
    private timezoneDispose?: () => void;
    private selected?: SettingsSelection;
    private readonly reads = new ReadScope();
    private readonly connections = new Connections();

    constructor(
        private readonly system: SystemResources,
        private readonly onNavigate: (provider: SettingsSelection) => void,
    ) {
        this.element.className = "settings-page";
        this.element.dataset.scroll = "settings";
        this.element.setAttribute("aria-labelledby", "settings-title");
        this.element.innerHTML = `<header class="settings-page-heading">
            <h1 id="settings-title" tabindex="-1">Settings</h1>
        </header>
        <label class="settings-section-select">Settings section
            <select id="settings-section" class="field-control select-control">
                ${settingsGroups.map(({ label, items }) => `<optgroup label="${label}">${items.map(({ id, label }) => `<option value="${id}">${label}</option>`).join("")}</optgroup>`).join("")}
            </select>
        </label>
        <div class="settings-layout">
            <nav class="settings-nav" aria-label="Settings sections">
                ${settingsGroups.map(({ label, items }) => `<div class="settings-nav-group"><p>${label}</p>${items.map(({ id, label, glyph }) => `<a href="${settingsHref(id)}" data-provider="${id}">${icon(glyph)}${label}</a>`).join("")}</div>`).join("")}
            </nav>
            <div class="settings-content">
                <a class="connection-back" href="/settings?section=connections" data-provider="connections" data-connection-back hidden>${icon("arrow")}Back to connections</a>
                ${settingsPanels.map((provider) => `<section data-provider-panel="${provider}" aria-label="${provider === "system" ? "System resources" : provider === "codex" ? "Codex connection" : provider === "github" ? "GitHub connection" : provider}" class="${provider === "codex" ? "codex-settings" : ""}" hidden></section>`).join("")}
            </div>
        </div>`;
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
        });
    }
    reset() {
        this.generation++;
        this.reads.reset();
        this.connections.reset();
        this.codexDispose?.();
        this.githubDispose?.();
        this.slackDispose?.();
        this.chatgptDispose?.();
        this.timezoneDispose?.();
        this.codexDispose =
            this.githubDispose =
            this.slackDispose =
            this.chatgptDispose =
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
        this.onNavigate(settingsTarget(value));
    }
    refreshConnections() {
        if (this.selected === "connections") void this.connections.refresh();
    }
    focusSelection(previous?: string) {
        const target =
            this.selected === "connections" &&
            previous &&
            isConnectionProvider(previous)
                ? this.element.querySelector<HTMLElement>(
                      `#connection-${previous}`,
                  )
                : this.element.querySelector<HTMLElement>(
                      "[data-provider-panel]:not([hidden]) h3, [data-provider-panel]:not([hidden]) h2",
                  );
        if (target) {
            if (target.matches("h2, h3")) target.tabIndex = -1;
            target.focus();
        } else
            this.element.querySelector<HTMLElement>("#settings-title")?.focus();
    }
    attach(host: HTMLElement, value: string) {
        host.append(this.element);
        const provider = settingsTarget(value);
        if (this.selected === provider) return;
        this.selected = provider;
        if (provider !== "connections") this.connections.deactivate();
        if (provider !== "slack") {
            this.slackDispose?.();
            this.slackDispose = undefined;
        }
        if (provider !== "chatgpt") {
            this.chatgptDispose?.();
            this.chatgptDispose = undefined;
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
        const parent = isConnectionProvider(provider)
            ? "connections"
            : provider;
        this.element.querySelector<HTMLElement>(
            "[data-connection-back]",
        )!.hidden = !isConnectionProvider(provider);
        this.element.querySelector<HTMLSelectElement>(
            "#settings-section",
        )!.value = parent;
        this.element
            .querySelectorAll<HTMLElement>(".settings-nav [data-provider]")
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
    private async loadTools(panel: HTMLElement) {
        const generation = this.generation;
        const tools = [
            {
                id: "cluster",
                label: "Cluster",
                href: "/headlamp/",
                glyph: "cube",
            },
            { id: "logs", label: "Logs", href: "/logs/", glyph: "file" },
        ];
        panel.innerHTML = `<nav class="settings-tools" aria-label="Operator tools">
            <h4>Operator tools</h4>
            <div class="settings-tool-links">${tools.map(({ id, label, glyph }) => `<a data-settings-tool="${id}" aria-disabled="true" target="_blank" rel="noopener">${icon(glyph)}<span>${label}</span>${icon("external")}</a>`).join("")}</div>
            <p data-tools-notice role="status">Checking tool availability…</p>
        </nav>`;
        const results = await Promise.allSettled(
            tools.map(({ id }) =>
                this.api<{ available: boolean }>(`/api/${id}`),
            ),
        );
        if (generation !== this.generation) return;
        const unavailable: string[] = [];
        results.forEach((result, index) => {
            const { id, label, href } = tools[index]!;
            const link = panel.querySelector<HTMLAnchorElement>(
                `[data-settings-tool="${id}"]`,
            )!;
            const available =
                result.status === "fulfilled"
                    ? result.value?.available
                    : undefined;
            if (available === true) {
                link.href = href;
                link.removeAttribute("aria-disabled");
                link.setAttribute(
                    "aria-label",
                    `${label} (opens in a new tab)`,
                );
            } else {
                const state =
                    available === false ? "not configured" : "unavailable";
                link.setAttribute("aria-label", `${label} (${state})`);
                unavailable.push(
                    available === false
                        ? `${label} is not configured for this installation.`
                        : `${label} availability could not be checked.`,
                );
            }
        });
        const notice = panel.querySelector<HTMLElement>("[data-tools-notice]")!;
        notice.hidden = unavailable.length === 0;
        notice.textContent = unavailable.join(" ");
    }
    private async mount(provider: SettingsSelection) {
        if (provider === "chatgpt") {
            this.chatgptDispose?.();
            this.chatgptDispose = mountChatGpt(
                this.element.querySelector<HTMLElement>(
                    '[data-provider-panel="chatgpt"]',
                )!,
            );
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
                panel.innerHTML = `<h3>Agents</h3>${agents.map((agent) => `<div class="provider-card"><div class="provider-heading">${icon("user")}<h4>${e(agent.name)}</h4>${agent.isDefault ? '<span class="provider-default">Default</span>' : ""}</div><p class="settings-description">${agent.model ? e(agent.model) : "Uses the connected runtime’s default model"}</p></div>`).join("") || '<p class="settings-description">No agents are available yet.</p>'}`;
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
            const panel = this.element.querySelector<HTMLElement>(
                '[data-provider-panel="system"]',
            )!;
            panel.innerHTML =
                "<h3>System</h3><div data-system-overview></div><div data-system-tools></div><div data-system-history></div>";
            this.system.mount(panel);
            void this.loadTools(
                panel.querySelector<HTMLElement>("[data-system-tools]")!,
            );
            return;
        }
        if (provider === "connections") {
            const panel = this.element.querySelector<HTMLElement>(
                '[data-provider-panel="connections"]',
            )!;
            panel.append(this.connections.element);
            void this.connections.refresh();
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
            panel.innerHTML = html;
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
                '<p class="settings-notice" role="alert">Codex settings could not be loaded. Return to Connections and try again.</p>';
            this.codexMounted = false;
        }
    }
}
