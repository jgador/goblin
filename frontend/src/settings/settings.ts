import { mountCodex } from "../connection/codex.js";
import { mountGitHub } from "./github.js";
import { icon } from "../work/presentation.js";

export class Settings {
    private readonly dialog = document.createElement("dialog");
    private codexMounted = false;
    private githubMounted = false;
    private opener: HTMLElement | null = null;
    private openerAction = "settings";
    constructor() {
        this.dialog.className = "settings-dialog";
        this.dialog.setAttribute("aria-labelledby", "settings-title");
        this.dialog.innerHTML = `<header class="settings-header"><h2 id="settings-title">Settings</h2><button class="settings-close" aria-label="Close settings">×</button></header><div class="settings-layout"><nav class="settings-nav" aria-label="Settings"><p>Connections</p><button data-provider="codex">${icon("spark")}Codex</button><button data-provider="github">${icon("branch")}GitHub</button></nav><div class="settings-content"><section class="codex-settings" data-provider-panel="codex" aria-label="Codex connection"><p>Loading Codex settings…</p></section><section data-provider-panel="github" aria-label="GitHub connection" hidden></section></div></div>`;
        document.body.append(this.dialog);
        this.dialog
            .querySelector(".settings-close")!
            .addEventListener("click", () => this.dialog.close());
        this.dialog.addEventListener("close", () => {
            const replacement = Array.from(
                document.querySelectorAll<HTMLElement>(
                    `[data-action="${this.openerAction}"]`,
                ),
            ).find((element) => element.getClientRects().length > 0);
            (this.opener?.isConnected ? this.opener : replacement)?.focus();
            window.dispatchEvent(new Event("goblin-connections-changed"));
        });
        this.dialog
            .querySelectorAll<HTMLButtonElement>("[data-provider]")
            .forEach((button) =>
                button.addEventListener("click", () => {
                    this.select(button.dataset.provider!);
                    void this.mount(button.dataset.provider!);
                }),
            );
    }
    private select(provider: string) {
        this.dialog
            .querySelectorAll<HTMLElement>("[data-provider-panel]")
            .forEach(
                (panel) =>
                    (panel.hidden = panel.dataset.providerPanel !== provider),
            );
        this.dialog
            .querySelectorAll<HTMLButtonElement>("[data-provider]")
            .forEach((button) => {
                button.classList.toggle(
                    "selected",
                    button.dataset.provider === provider,
                );
                button.setAttribute(
                    "aria-current",
                    button.dataset.provider === provider ? "page" : "false",
                );
            });
    }
    async open(provider = "codex") {
        this.opener = document.activeElement as HTMLElement;
        this.openerAction = this.opener?.dataset.action ?? "settings";
        this.select(provider);
        if (!this.dialog.open) this.dialog.showModal();
        await this.mount(provider);
    }
    private async mount(provider: string) {
        if (provider === "github") {
            if (!this.githubMounted) {
                this.githubMounted = true;
                mountGitHub(
                    this.dialog.querySelector<HTMLElement>(
                        '[data-provider-panel="github"]',
                    )!,
                );
            }
            return;
        }
        if (this.codexMounted) return;
        this.codexMounted = true;
        const panel = this.dialog.querySelector<HTMLElement>(
            '[data-provider-panel="codex"]',
        )!;
        try {
            const response = await fetch("/connection/panel.html");
            if (!response.ok) throw new Error();
            panel.innerHTML = await response.text();
            await mountCodex(panel);
        } catch {
            panel.textContent =
                "Codex settings could not be loaded. Refresh Goblin and try again.";
            this.codexMounted = false;
        }
    }
}
