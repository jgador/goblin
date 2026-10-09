import { SlackSetupStatus } from "../api/values.js";
import { requestJson, errorMessage } from "../api/client.js";
import { escapeHtml as e } from "../work/presentation.js";
import type { SlackState } from "../api/integration-contracts.js";

export function mountSlack(root: HTMLElement) {
    const controller = new AbortController();
    let state: SlackState | undefined,
        busy = false,
        disposed = false,
        error = "",
        code = "",
        copied = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let renderedState = "";
    const api = <T>(path = "", body?: unknown) =>
        requestJson<T>(`/api/integrations/slack${path}`, {
            body,
            signal: controller.signal,
            failureMessage: "Slack settings could not be updated. Try again.",
            onUnauthorized: () =>
                window.dispatchEvent(new Event("goblin-workspace-locked")),
        });
    const button = (action: string, label: string, primary = false) =>
        `<button type="button" class="${primary ? "settings-primary" : "settings-secondary"}" data-slack="${action}" ${busy ? "disabled" : ""}>${label}</button>`;
    function render() {
        if (disposed) return;
        renderedState = JSON.stringify(state);
        const active = root.contains(document.activeElement)
            ? (document.activeElement as HTMLElement)
            : null;
        const focus = active?.dataset.slack;
        const expanded = root.querySelector("details")?.open ?? false;
        const manual = `<details ${expanded ? "open" : ""}><summary>Connect an existing app</summary><p class="settings-description">Use this if your workspace requires manual setup or you already created Goblin.</p><ol class="slack-steps"><li><a href="/api/integrations/slack/manifest" download="goblin-slack-manifest.json">Download the Goblin manifest</a> and <a href="https://api.slack.com/apps?new_app=1" target="_blank" rel="noopener noreferrer">create an app from it in Slack</a>.</li><li>Under Basic Information, create an app-level token with <code>connections:write</code>.</li><li>Install the app into your workspace and copy its bot token.</li></ol><form data-slack-form="tokens"><label>App-level token<input name="appToken" type="password" autocomplete="off" required placeholder="xapp-…"></label><label>Bot token<input name="botToken" type="password" autocomplete="off" required placeholder="xoxb-…"></label><button class="settings-primary" ${busy ? "disabled" : ""}>Verify and connect</button></form></details>`;
        let content =
            '<p class="settings-description">Loading Slack settings…</p>';
        if (state && !state.available)
            content =
                '<p class="settings-notice">Slack requires durable Work storage. Configure PostgreSQL and enable Work to connect Slack.</p>';
        else if (state) {
            const { connection: connection, setup } = state;
            const appId = connection.appId ?? setup.appId;
            const appLink = appId
                ? `https://api.slack.com/apps/${encodeURIComponent(appId)}`
                : "https://api.slack.com/apps";
            const branding = `<div class="slack-branding"><img src="/assets/branding/slack.png" alt="Goblin app icon" width="48" height="48"><p><a href="/assets/branding/slack.png" download="goblin-slack.png">Download Goblin icon</a><br><span class="settings-description">Optional: add it in <a href="${appLink}" target="_blank" rel="noopener noreferrer">Slack app settings</a> → Basic Information → Display Information.</span></p></div>`;
            if (connection.appId) {
                content = `<div class="provider-card"><h4>${e(connection.workspace ?? "Slack workspace")}</h4><p role="status">${e(connection.status)} · Bot ${e(connection.botUserId ?? "")}</p>${connection.notice ? `<p class="settings-notice">${e(connection.notice)}</p>` : ""}${setup.notice ? `<p class="settings-notice">${e(setup.notice)}</p>` : ""}${button("disconnect", "Disconnect Slack")}</div><h4>Slack identities</h4><p class="settings-description">Linked Slack users act as this deployment’s local owner. Only link people who should have that access. Results and repository approvals are opened in Goblin.</p>${state.identities.map((identity) => `<div class="slack-identity"><span>${e(identity.userId)} → local owner</span>${button(`revoke:${identity.id}`, "Remove access")}</div>`).join("")}${state.link?.userId ? `<div class="provider-card"><p>Slack user <strong>${e(state.link.userId)}</strong> sent the linking code. Confirm this is the identity you intended to authorize.</p>${button(`confirm-link:${state.link.id}`, "Grant local owner access", true)}</div>` : code && state.link ? `<p>Send this as a direct message to Goblin in Slack:</p><label class="slack-code">Linking command<input readonly value="link ${e(code)}" aria-label="Slack linking command"></label>${button("copy-link", copied ? "Copied" : "Copy linking command")}<p class="settings-description">Expires in five minutes. Return here to confirm the Slack identity.</p>` : button("link", "Link a Slack identity", true)}<p class="settings-description">Send a DM or mention @goblin to start Work. Reply in the same thread to continue; mention @goblin again in channel replies.</p>${branding}`;
            } else {
                switch (setup.status) {
                    case SlackSetupStatus.Idle:
                    case SlackSetupStatus.Complete:
                        content = `${button("setup", setup.appId ? "Create another Slack app" : "Connect Slack", true)}<p class="settings-description">Authorize once in Slack. Goblin creates and installs an app owned by your workspace.</p>`;
                        break;
                    case SlackSetupStatus.Preparing:
                        content = `<p role="status">Preparing Slack setup…</p>${button("cancel", "Cancel setup")}`;
                        break;
                    case SlackSetupStatus.AwaitingAuthorization:
                        content = setup.command
                            ? `<ol class="slack-steps"><li>Open your Slack workspace. Paste this command into the message box in your DM with yourself, then press Enter.</li><li>Approve Slack’s permissions dialog.</li><li>Enter the confirmation code Slack gives you.</li></ol><label class="slack-code">Slack command<input readonly value="${e(setup.command)}" aria-label="Slack authorization command"></label>${button("copy", copied ? "Copied" : "Copy command")}<form data-slack-form="confirmation"><label>Confirmation code<input name="code" autocomplete="off" required maxlength="64" autofocus></label><button class="settings-primary" ${busy ? "disabled" : ""}>Finish setup</button></form>${button("cancel", "Cancel setup")}`
                            : `<p>Setup is open in another Goblin session.</p>${button("cancel", "Cancel setup")}`;
                        break;
                    case SlackSetupStatus.Installing:
                        content = `<p role="status">Creating and installing your Slack app…</p><p class="settings-description">Your workspace may require administrator approval. You can keep this page open while it completes.</p>${button("cancel", "Cancel setup")}`;
                        break;
                    case SlackSetupStatus.NeedsAttention:
                        content = `<p class="settings-notice">${e(setup.notice ?? "Review Slack setup before continuing.")}</p>${appId ? `<a href="${appLink}" target="_blank" rel="noopener noreferrer">Review the existing app in Slack</a>${button("resume", "Resume setup", true)}` : ""}${button("cancel", "Cancel setup")}`;
                        break;
                }
                content += `${setup.notice && setup.status !== SlackSetupStatus.NeedsAttention ? `<p class="settings-notice">${e(setup.notice)}</p>` : ""}${manual}${branding}`;
            }
        }
        root.innerHTML = `<h3>Slack</h3><p class="settings-description">Talk to your self-hosted AI coworker from Slack. Your workspace owns its app; Goblin connects from your deployment. Agent questions appear in the original Slack thread, where linked users can reply. Results and repository approvals open in Goblin.</p>${error ? `<p class="settings-notice" role="alert">${e(error)}</p>` : ""}<div class="slack-settings">${content}</div>`;
        if (focus)
            [...root.querySelectorAll<HTMLElement>("[data-slack]")]
                .find((element) => element.dataset.slack === focus)
                ?.focus();
    }
    async function refresh(force = false) {
        try {
            const next = await api<SlackState>();
            if (disposed) return;
            const changed = JSON.stringify(next) !== renderedState;
            state = next;
            // Polling must not erase a confirmation code or token being entered.
            if (
                force ||
                (changed &&
                    !(
                        document.activeElement instanceof HTMLInputElement &&
                        root.contains(document.activeElement)
                    ))
            )
                render();
        } catch (failure) {
            if (!disposed) {
                error = errorMessage(failure, "Could not load Slack settings.");
                render();
            }
        }
    }
    async function action(path: string, body: unknown = {}) {
        if (busy || disposed) return;
        busy = true;
        error = "";
        root.querySelectorAll<HTMLButtonElement>("button").forEach(
            (button) => (button.disabled = true),
        );
        try {
            const result = await api<{ code?: string }>(path, body);
            if (path === "/link") code = result.code ?? "";
            if (path === "/disconnect" || path === "/link/confirm") code = "";
        } catch (failure) {
            error = errorMessage(
                failure,
                "Slack setup could not be confirmed.",
            );
        } finally {
            busy = false;
            await refresh(true);
        }
    }
    async function click(event: Event) {
        const target = (event.target as HTMLElement).closest<HTMLElement>(
            "[data-slack]",
        );
        if (!target) return;
        const command = target.dataset.slack!;
        if (command === "copy" || command === "copy-link") {
            const value =
                command === "copy" ? state?.setup?.command : `link ${code}`;
            if (!value) return;
            try {
                await navigator.clipboard.writeText(value);
                copied = true;
                render();
            } catch {
                root.querySelector<HTMLInputElement>(
                    ".slack-code input",
                )?.select();
            }
        } else if (command.startsWith("confirm-link:"))
            await action("/link/confirm", { id: command.split(":")[1] });
        else if (command.startsWith("revoke:"))
            await action("/link/revoke", { id: command.split(":")[1] });
        else {
            copied = false;
            await action(`/${command}`);
        }
    }
    async function submit(event: Event) {
        const form = event.target as HTMLFormElement;
        if (!form.dataset.slackForm) return;
        event.preventDefault();
        const values = new FormData(form);
        if (form.dataset.slackForm === "confirmation")
            await action("/confirm", {
                code: String(values.get("code") ?? "").trim(),
            });
        else {
            const body = {
                appToken: values.get("appToken"),
                botToken: values.get("botToken"),
            };
            form.reset();
            await action("/connect", body);
        }
    }
    async function poll() {
        await refresh();
        if (!disposed) timer = setTimeout(poll, 2000);
    }
    root.addEventListener("click", click);
    root.addEventListener("submit", submit);
    render();
    void poll();
    return () => {
        disposed = true;
        controller.abort();
        clearTimeout(timer);
        code = "";
        state = undefined;
        root.replaceChildren();
        root.removeEventListener("click", click);
        root.removeEventListener("submit", submit);
    };
}
