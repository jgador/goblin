import { errorMessage, requestJson } from "../api/client.js";
import type {
    ChatGptState,
    ChatGptVerification,
} from "../api/chatgpt-contracts.js";
import { escapeHtml as e } from "../work/presentation.js";
import {
    action,
    callbackFeedback,
    callbackSaved,
    completed,
    confirmations,
    setupSteps,
    stepContent,
    type Confirmation,
    type Workflow,
} from "./chatgpt-workflow.js";

const base = "/api/integrations/chatgpt";
const progressKey = "goblin.chatgpt.full-work-setup";
const stepHints = [
    "Enable your instance",
    "Prepare secure access",
    "Connect in ChatGPT",
    "Try a conversation",
];

export function mountChatGpt(root: HTMLElement) {
    const controller = new AbortController();
    let disposed = false;
    let busy = false;
    let state: ChatGptState | undefined;
    let verification: ChatGptVerification | undefined;
    let step = 0;
    let confirmed = new Set<Confirmation>();
    let scope = "";
    let config = "";
    let clientSecret = "";
    let notice = "";
    let noticeTone: "info" | "error" = "info";
    let storageAvailable = true;
    let discardDrafts = false;
    const drafts = new Map<string, FormData>();
    const openDetails = new Set<string>();
    const api = <T>(suffix = "", body?: unknown) =>
        requestJson<T>(base + suffix, {
            body,
            signal: controller.signal,
            onUnauthorized: () =>
                window.dispatchEvent(new Event("goblin-workspace-locked")),
        });
    const model = (): Workflow => ({
        state: state!,
        verification,
        confirmed,
        step,
        config,
        clientSecret,
        callbackValue: drafts.has("callback")
            ? String(drafts.get("callback")!.get("redirectUri") ?? "")
            : undefined,
    });

    function restoreProgress() {
        try {
            const saved = JSON.parse(
                localStorage.getItem(progressKey) ?? "null",
            );
            if (saved?.scope !== scope) return;
            step =
                Number.isInteger(saved.step) &&
                saved.step >= 0 &&
                saved.step < setupSteps.length
                    ? saved.step
                    : 0;
            confirmed = new Set(
                confirmations.filter(
                    (key) =>
                        Array.isArray(saved.confirmed) &&
                        saved.confirmed.includes(key),
                ),
            );
        } catch {
            storageAvailable = false;
        }
    }
    function saveProgress() {
        try {
            localStorage.setItem(
                progressKey,
                JSON.stringify({
                    scope,
                    step,
                    confirmed: [...confirmed],
                }),
            );
        } catch {
            storageAvailable = false;
        }
    }
    function resetConnectionTests() {
        confirmed.delete("listed");
        confirmed.delete("written");
        confirmed.delete("revoked");
        saveProgress();
    }
    function render(focusId?: string) {
        if (disposed) return;
        if (!discardDrafts) {
            for (const form of root.querySelectorAll<HTMLFormElement>(
                "form[data-chatgpt-form]",
            ))
                drafts.set(form.dataset.chatgptForm!, new FormData(form));
            for (const details of root.querySelectorAll<HTMLDetailsElement>(
                "details",
            )) {
                const key = details.querySelector("summary")?.textContent ?? "";
                if (details.open) openDetails.add(key);
                else openDetails.delete(key);
            }
        }
        discardDrafts = false;
        const done = state ? completed(model()) : [];
        root.innerHTML = `<header class="chatgpt-heading"><span class="chatgpt-logo" aria-hidden="true"><img src="/assets/providers/chatgpt.svg" width="28" height="28" alt=""></span><div><h3>ChatGPT plugin</h3><p class="settings-description">Read and manage all Work through your private connection.</p></div></header>
            <p class="chatgpt-notice" role="status" data-chatgpt-notice data-tone="${noticeTone}" ${notice ? "" : "hidden"}>${e(notice)}</p>
            ${
                !state
                    ? `<div class="chatgpt-loading"><p data-chatgpt-loading>${noticeTone === "error" ? "Connection details could not be loaded." : "Loading your connection details…"}</p>` +
                      action("reload", "Reload setup") +
                      "</div>"
                    : `<div class="chatgpt-settings">
                <div class="chatgpt-summary"><span class="chatgpt-server-status" data-enabled="${state.configured}"><i aria-hidden="true"></i>${state.configured ? "Server enabled" : "Setup required"}</span><span>Read and manage all Work</span></div>
                <div class="chatgpt-workflow">
                <nav class="chatgpt-progress" aria-label="Plugin setup progress"><p class="chatgpt-rail-label">YOUR CONNECTION</p><ol class="chatgpt-stepper">${setupSteps
                    .map(
                        (label, i) =>
                            `<li><button type="button" id="chatgpt-step-${i}" data-chatgpt="step" data-step="${i}" data-complete="${done[i]}" ${i === step ? 'aria-current="step"' : ""}><span class="chatgpt-step-number" aria-hidden="true">${done[i] ? "✓" : i + 1}</span><span>${label}<small>${done[i] ? ([0, 1].includes(i) ? "Checked in Goblin" : "You confirmed") : stepHints[i]}</small></span></button></li>`,
                    )
                    .join(
                        "",
                    )}</ol><label class="chatgpt-mobile-step"><span id="chatgpt-step-label">Setup step</span><select aria-labelledby="chatgpt-step-label" data-chatgpt-step ${busy ? "disabled" : ""}>${setupSteps
                    .map(
                        (label, i) =>
                            `<option value="${i}" ${i === step ? "selected" : ""}>${i + 1}. ${label}${done[i] ? " · Done" : ""}</option>`,
                    )
                    .join("")}</select></label></nav>
                <div class="chatgpt-step-panel"><div class="chatgpt-step-body"><p class="chatgpt-step-caption">Step ${step + 1} of ${setupSteps.length}</p>${stepContent(model())}</div>
                <div class="chatgpt-step-footer">${action("back", "Back", false, step === 0)}<span>${done.filter(Boolean).length} of ${setupSteps.length} steps complete</span>${step < setupSteps.length - 1 ? action("next", `Continue to ${setupSteps[step + 1]}`, true, !done[step]) : ""}</div></div>
                </div>
                <details class="chatgpt-help"><summary>Need help with this step?</summary><div><p><strong>Cannot connect:</strong> check your HTTPS address and ChatGPT workspace permissions. A tunnel carries MCP traffic; sign-in still needs reachable HTTPS.</p><p><strong>Sign-in fails:</strong> check the saved callback and client secret. Unlock Goblin with your owner password and approve full Work access.</p><p><strong>Work is unavailable:</strong> refresh the connection in ChatGPT, then reconnect if it still has an old read-only token.</p><p><strong>Tools changed:</strong> refresh the custom connection in ChatGPT and start a new conversation.</p>${state.configured ? action("client/revoke", "Revoke ChatGPT connections") : ""}</div></details>
                <div class="chatgpt-meta"><p class="chatgpt-progress-note">${storageAvailable ? "Progress is saved in this browser." : "Progress stays on this page; browser storage is unavailable."} You confirm installation and conversation tests.</p>${action("restart-setup", "Start setup again")}</div>
            </div>`
            }`;
        for (const form of root.querySelectorAll<HTMLFormElement>(
            "form[data-chatgpt-form]",
        )) {
            const values = drafts.get(form.dataset.chatgptForm!);
            if (!values) continue;
            for (const control of form.querySelectorAll<
                HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement
            >("input, select, textarea")) {
                if (
                    control instanceof HTMLInputElement &&
                    control.type === "checkbox"
                )
                    control.checked = values.has(control.name);
                else control.value = String(values.get(control.name) ?? "");
            }
        }
        for (const details of root.querySelectorAll<HTMLDetailsElement>(
            "details",
        ))
            details.open = openDetails.has(
                details.querySelector("summary")?.textContent ?? "",
            );
        if (busy)
            root.querySelectorAll<HTMLButtonElement>("button").forEach(
                (button) => (button.disabled = true),
            );
        if (focusId)
            root.querySelector<HTMLElement>(`#${CSS.escape(focusId)}`)?.focus();
    }
    function announce(message: string, tone: "info" | "error" = "info") {
        if (disposed) return;
        notice = message;
        noticeTone = tone;
        const element = root.querySelector<HTMLElement>(
            "[data-chatgpt-notice]",
        );
        if (element) {
            element.textContent = message;
            element.dataset.tone = tone;
            element.hidden = !message;
        }
    }
    function updateCallback() {
        const current = model();
        const saved = callbackSaved(current);
        const task = root.querySelector<HTMLElement>(".chatgpt-callback-task");
        if (task) task.dataset.callbackSaved = String(saved);
        const label = root.querySelector<HTMLElement>("[data-callback-label]");
        if (label)
            label.textContent = saved ? "Callback saved" : "Required in Goblin";
        const status = root.querySelector<HTMLElement>(
            "#chatgpt-callback-status",
        );
        const feedback = callbackFeedback(current);
        if (status && status.textContent !== feedback)
            status.textContent = feedback;
        for (const [selector, hidden] of [
            ["[data-callback-blocked]", saved],
            ["[data-callback-ready]", !saved],
        ] as const) {
            const element = root.querySelector<HTMLElement>(selector);
            if (element) element.hidden = hidden;
        }
        const installed = root.querySelector<HTMLInputElement>(
            "#chatgpt-confirm-installed",
        );
        if (installed) {
            installed.disabled = busy || !saved;
            installed.checked = saved && confirmed.has("installed");
        }
        const done = completed(current);
        const next = root.querySelector<HTMLButtonElement>(
            '[data-chatgpt="next"]',
        );
        if (next) next.disabled = busy || !done[step];
        const progress = root.querySelector<HTMLElement>(
            ".chatgpt-step-footer > span",
        );
        if (progress)
            progress.textContent = `${done.filter(Boolean).length} of ${setupSteps.length} steps complete`;
        const install = root.querySelector<HTMLElement>(
            '.chatgpt-stepper [data-step="2"]',
        );
        const number = install?.querySelector(".chatgpt-step-number");
        if (number) number.textContent = done[2] ? "✓" : "3";
        const confirmation = install?.querySelector("small");
        if (confirmation)
            confirmation.textContent = done[2] ? "You confirmed" : stepHints[2];
        if (install) install.dataset.complete = String(done[2]);
        const option = root.querySelector<HTMLOptionElement>(
            '[data-chatgpt-step] option[value="2"]',
        );
        if (option)
            option.textContent = `3. Install${done[2] ? " · Done" : ""}`;
    }
    async function refresh() {
        const next = await api<ChatGptState>();
        if (disposed) return;
        const nextScope = JSON.stringify([
            next.issuer,
            next.serverUrl,
            next.ownerSubject,
        ]);
        if (nextScope !== scope) {
            scope = nextScope;
            verification = undefined;
            confirmed.clear();
            step = 0;
            clientSecret = "";
            drafts.delete("client");
            drafts.delete("callback");
            discardDrafts = true;
            restoreProgress();
        }
        state = next;
    }
    async function run(operation: () => Promise<void>) {
        if (busy || disposed) return;
        const focused = root.contains(document.activeElement)
            ? document.activeElement?.id
            : undefined;
        busy = true;
        root.setAttribute("aria-busy", "true");
        root.querySelectorAll<HTMLButtonElement>("button").forEach(
            (button) => (button.disabled = true),
        );
        root.querySelector<HTMLSelectElement>(
            "[data-chatgpt-step]",
        )?.setAttribute("disabled", "");
        try {
            await operation();
        } catch (error) {
            announce(
                errorMessage(error, "ChatGPT setup could not be confirmed."),
                "error",
            );
        } finally {
            busy = false;
            root.removeAttribute("aria-busy");
            render(focused);
        }
    }
    function navigate(nextStep: number) {
        step = nextStep;
        notice = "";
        saveProgress();
        render("chatgpt-step-title");
    }
    function click(event: Event) {
        if (!(event.target instanceof Element)) return;
        const button = event.target.closest<HTMLButtonElement>(
            "button[data-chatgpt]",
        );
        if (!button || button.disabled) return;
        const name = button.dataset.chatgpt;
        if (busy) return;
        if (name === "restart-setup") {
            confirmed.clear();
            drafts.clear();
            openDetails.clear();
            discardDrafts = true;
            config = "";
            clientSecret = "";
            verification = undefined;
            navigate(0);
            announce(
                "Setup progress cleared. Saved OAuth settings and connections remain available.",
            );
            return;
        }
        if (name === "step") return navigate(Number(button.dataset.step));
        if (name === "prepare") return navigate(0);
        if (name === "provider") return navigate(1);
        if (name === "back") return navigate(Math.max(0, step - 1));
        if (name === "next" && state && completed(model())[step]) {
            return navigate(Math.min(setupSteps.length - 1, step + 1));
        }
        if (name === "copy") {
            const input = root.querySelector<
                HTMLInputElement | HTMLTextAreaElement
            >(`[data-copy-value="${button.dataset.key}"]`);
            if (!input) return;
            void navigator.clipboard?.writeText(input.value).then(
                () => announce("Copied."),
                () => {
                    input.focus();
                    input.select();
                    announce("Select and copy the highlighted value.");
                },
            );
            if (!navigator.clipboard) {
                input.focus();
                input.select();
                announce("Select and copy the highlighted value.");
            }
            return;
        }
        void run(async () => {
            if (name === "verify") {
                verification = undefined;
                announce("Checking provider and storage…");
                verification = await api<ChatGptVerification>("/verify", {});
                announce(
                    verification.discovery &&
                        verification.database &&
                        verification.issuerResponses
                        ? "Provider and storage checks passed. Continue to install in ChatGPT."
                        : "Some checks need attention. Follow the fixes below and check again.",
                );
            } else if (name === "client/revoke") {
                await api("/client/revoke", {});
                confirmed.delete("installed");
                resetConnectionTests();
                announce(
                    "ChatGPT connections revoked. Reconnect and approve access to use the plugin again.",
                );
            } else if (name === "reload") {
                await refresh();
                announce(
                    state?.configured
                        ? "Configuration loaded."
                        : "The integration is not enabled yet. Apply the installation settings and restart Goblin, then check again.",
                );
            }
        });
    }
    function change(event: Event) {
        const input = event.target;
        if (busy) return;
        if (
            input instanceof HTMLSelectElement &&
            input.hasAttribute("data-chatgpt-step")
        )
            return navigate(Number(input.value));
        if (!(input instanceof HTMLInputElement)) return;
        const key = confirmations.find(
            (value) => value === input.dataset.confirm,
        );
        if (!key) return;
        if (input.checked) confirmed.add(key);
        else confirmed.delete(key);
        saveProgress();
        render(input.id);
    }
    function input(event: Event) {
        if (
            event.target instanceof HTMLInputElement &&
            event.target.id === "chatgpt-callback-url" &&
            event.target.form
        ) {
            drafts.set("callback", new FormData(event.target.form));
            updateCallback();
        }
    }
    function submit(event: Event) {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) || !form.dataset.chatgptForm)
            return;
        event.preventDefault();
        if (busy) return;
        const data = new FormData(form);
        if (form.dataset.chatgptForm === "callback") {
            const callback = form.querySelector<HTMLInputElement>(
                "#chatgpt-callback-url",
            );
            if (callback) callback.readOnly = true;
        }
        void run(async () => {
            if (form.dataset.chatgptForm === "instance") {
                const origin = httpsAddress(String(data.get("origin") ?? ""));
                if (origin.pathname !== "/")
                    throw new Error("Use a Goblin origin without a path.");
                config = `env:\n  - name: ${state!.environmentVariables.publicOrigin}\n    value: ${JSON.stringify(origin.origin)}\n  - name: ${state!.environmentVariables.enabled}\n    value: "true"`;
                announce(
                    "Settings generated. Apply them to your Goblin application container, then restart it.",
                );
            } else if (
                form.dataset.chatgptForm === "client" ||
                form.dataset.chatgptForm === "callback"
            ) {
                const savingCallback = form.dataset.chatgptForm === "callback";
                const redirectUri = savingCallback
                    ? String(data.get("redirectUri") ?? "").trim()
                    : (state!.redirectUri ?? "");
                const result = await api<{ clientSecret: string | null }>(
                    "/client",
                    {
                        redirectUri,
                        rotateSecret:
                            !savingCallback && data.has("rotateSecret"),
                    },
                );
                if (disposed) return;
                if (result.clientSecret) clientSecret = result.clientSecret;
                if (
                    result.clientSecret ||
                    redirectUri !== (state!.redirectUri ?? "")
                ) {
                    confirmed.delete("installed");
                    resetConnectionTests();
                }
                await refresh();
                drafts.delete(form.dataset.chatgptForm);
                discardDrafts = true;
                announce(
                    savingCallback
                        ? "OAuth callback saved. Return to ChatGPT to start sign-in. If an earlier request failed, restart the connection there."
                        : "OAuth client saved. Save the generated secret, then continue to Install to get ChatGPT's callback.",
                );
            }
        });
    }
    root.addEventListener("click", click);
    root.addEventListener("change", change);
    root.addEventListener("input", input);
    root.addEventListener("submit", submit);
    render();
    void run(refresh);
    return () => {
        disposed = true;
        controller.abort();
        root.removeEventListener("click", click);
        root.removeEventListener("change", change);
        root.removeEventListener("input", input);
        root.removeEventListener("submit", submit);
        root.replaceChildren();
    };
}
function httpsAddress(value: string): URL {
    let url: URL;
    try {
        url = new URL(value.trim());
    } catch {
        throw new Error("Enter a complete HTTPS Goblin address.");
    }
    if (
        url.protocol !== "https:" ||
        url.username ||
        url.password ||
        url.search ||
        url.hash
    )
        throw new Error(
            "Use HTTPS addresses without credentials, a query, or a fragment.",
        );
    return url;
}
