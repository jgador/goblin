import { errorMessage, requestJson } from "../api/client.js";
import type {
    ChatGptState,
    ChatGptVerification,
    ChatGptPackage,
} from "../api/chatgpt-contracts.js";
import { escapeHtml as e } from "../work/presentation.js";
import {
    action,
    completed,
    confirmations,
    privateSteps,
    publicSteps,
    stepContent,
    type Confirmation,
    type SetupPath,
    type Workflow,
} from "./chatgpt-workflow.js";

const base = "/api/integrations/chatgpt";
const progressKey = "goblin.chatgpt.setup";

export function mountChatGpt(root: HTMLElement) {
    const controller = new AbortController();
    let disposed = false;
    let busy = false;
    let state: ChatGptState | undefined;
    let work: Workflow["work"] = [];
    let verification: ChatGptVerification | undefined;
    let path: SetupPath = "private";
    let step = 0;
    let confirmed = new Set<Confirmation>();
    let scope = "";
    let config = "";
    let template = "";
    let pluginPackage: ChatGptPackage | undefined;
    let notice = "";
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
        work,
        verification,
        confirmed,
        path,
        step,
        config,
        template,
        package: pluginPackage,
    });
    const steps = () => (path === "private" ? privateSteps : publicSteps);

    function restoreProgress() {
        try {
            const saved = JSON.parse(
                localStorage.getItem(progressKey) ?? "null",
            );
            if (saved?.scope !== scope) return;
            path = saved.path === "public" ? "public" : "private";
            step =
                Number.isInteger(saved.step) &&
                saved.step >= 0 &&
                saved.step < steps().length
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
                    path,
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
        confirmed.delete("status");
        confirmed.delete("denied");
        saveProgress();
    }
    function render(focusId?: string) {
        if (disposed) return;
        if (!discardDrafts)
            for (const form of root.querySelectorAll<HTMLFormElement>(
                "form[data-chatgpt-form]",
            ))
                drafts.set(form.dataset.chatgptForm!, new FormData(form));
        discardDrafts = false;
        for (const details of root.querySelectorAll<HTMLDetailsElement>(
            "details",
        )) {
            const key = details.querySelector("summary")?.textContent ?? "";
            if (details.open) openDetails.add(key);
            else openDetails.delete(key);
        }
        const done = state ? completed(model()) : [];
        root.innerHTML = `<h3>ChatGPT plugin</h3><p class="settings-description">Bring shared Work into ChatGPT. Set up your private plugin, then publish it publicly if you choose.</p>
            <p class="settings-notice" role="status" data-chatgpt-notice ${notice ? "" : "hidden"}>${e(notice)}</p>
            ${
                !state
                    ? `<p data-chatgpt-loading>Loading setup…</p>${action("reload", "Reload setup")}`
                    : `<div class="chatgpt-settings">
                <div class="chatgpt-paths" role="group" aria-label="Setup path"><button type="button" data-chatgpt="private" aria-pressed="${path === "private"}">Private plugin<span>Connect your own instance</span></button><button type="button" data-chatgpt="public" aria-pressed="${path === "public"}">Publish publicly<span>Apply to the plugin directory</span></button></div>
                <nav aria-label="Plugin setup progress"><ol class="chatgpt-stepper">${steps()
                    .map(
                        (label, i) =>
                            `<li><button type="button" data-chatgpt="step" data-step="${i}" ${i === step ? 'aria-current="step"' : ""}><span class="chatgpt-step-number" aria-hidden="true">${done[i] ? "✓" : i + 1}</span><span>${label}<small>${done[i] ? (path === "private" && (i === 0 || i === 1 || i === 3) ? "Checked in Goblin" : "You confirmed") : ""}</small></span></button></li>`,
                    )
                    .join(
                        "",
                    )}</ol><label class="chatgpt-mobile-step"><span id="chatgpt-step-label">Setup step</span><select aria-labelledby="chatgpt-step-label" data-chatgpt-step ${busy ? "disabled" : ""}>${steps()
                    .map(
                        (label, i) =>
                            `<option value="${i}" ${i === step ? "selected" : ""}>${i + 1}. ${label}${done[i] ? " · Done" : ""}</option>`,
                    )
                    .join("")}</select></label></nav>
                <div class="chatgpt-step-body"><p class="chatgpt-step-caption">${path === "private" ? "PRIVATE SETUP" : "PUBLIC SUBMISSION"} · STEP ${step + 1} OF ${steps().length}</p>${stepContent(model())}</div>
                <div class="chatgpt-step-footer">${action("back", "Back", false, step === 0)}<span>${done.filter(Boolean).length} of ${steps().length} steps complete</span>${step < steps().length - 1 ? action("next", `Continue to ${steps()[step + 1]}`, true, !done[step]) : ""}</div>
                <p class="chatgpt-progress-note">${storageAvailable ? "Your place and confirmations are saved in this browser." : "Progress is kept while this page is open; browser storage is unavailable."} ChatGPT installation and publication are confirmed by you.</p>
                <details><summary>Need help with this step?</summary><p><strong>Cannot connect:</strong> check the HTTPS server URL, certificate, proxy Host forwarding, and ChatGPT workspace permissions. A tunnel carries MCP traffic; it does not make Keycloak reachable.</p><p><strong>Sign-in fails:</strong> check the exact callback in Keycloak, the client secret, required PKCE S256, resource indicators, and read scope. Access tokens need RS256, <code>typ=at+jwt</code>, the exact MCP resource audience, subject, and expiry.</p><p><strong>Empty list or access denied:</strong> check the immutable user ID, grant expiry, and selected Work. Signing in again does not create grants.</p><p><strong>Tools changed:</strong> open the custom connection in ChatGPT Plugins, select Refresh, and start a new conversation.</p>${state.configured ? `<p><strong>Changed signing keys:</strong> retire the affected key at Keycloak, then clear Goblin’s cache. Reads already in progress may finish.</p>${action("keys/refresh", "Clear signing-key cache")}` : ""}<p><a href="https://developers.openai.com/plugins/deploy/connect-chatgpt" target="_blank" rel="noopener noreferrer">Official ChatGPT connection help ↗</a></p></details>
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
    function announce(message: string) {
        if (disposed) return;
        notice = message;
        const element = root.querySelector<HTMLElement>(
            "[data-chatgpt-notice]",
        );
        if (element) {
            element.textContent = message;
            element.hidden = !message;
        }
    }
    async function refresh() {
        const next = await api<ChatGptState>();
        const choices = next.configured ? await api<typeof work>("/work") : [];
        if (disposed) return;
        const nextScope = JSON.stringify([next.issuer, next.serverUrl]);
        if (nextScope !== scope) {
            scope = nextScope;
            verification = undefined;
            confirmed.clear();
            path = "private";
            step = 0;
            template = "";
            pluginPackage = undefined;
            drafts.delete("provider");
            drafts.delete("grant");
            discardDrafts = true;
            restoreProgress();
        }
        state = next;
        work = choices;
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
            );
        } finally {
            busy = false;
            root.removeAttribute("aria-busy");
            render(focused);
        }
    }
    function navigate(nextStep: number, nextPath = path) {
        path = nextPath;
        step = nextStep;
        notice = "";
        saveProgress();
        render("chatgpt-step-title");
    }
    function click(event: Event) {
        if (!(event.target instanceof Element) || busy) return;
        const button = event.target.closest<HTMLButtonElement>(
            "button[data-chatgpt]",
        );
        if (!button || button.disabled) return;
        const name = button.dataset.chatgpt;
        if (name === "step") return navigate(Number(button.dataset.step));
        if (name === "private" || name === "public") return navigate(0, name);
        if (name === "prepare") return navigate(0, "private");
        if (name === "share") return navigate(3, "private");
        if (name === "back") return navigate(Math.max(0, step - 1));
        if (name === "next" && state && completed(model())[step]) {
            const form = root.querySelector<HTMLFormElement>(
                'form[data-chatgpt-form="listing"], form[data-chatgpt-form="review"]',
            );
            if (form && !form.reportValidity()) return;
            return navigate(Math.min(steps().length - 1, step + 1));
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
        if (name === "download-package" && pluginPackage) {
            saveFile(
                new Blob(
                    [
                        Uint8Array.from(
                            atob(pluginPackage.archiveBase64),
                            (c) => c.charCodeAt(0),
                        ),
                    ],
                    { type: "application/zip" },
                ),
                "goblin-plugin.zip",
            );
            announce(
                "Plugin ZIP saved. Upload it in the OpenAI submission portal.",
            );
            return;
        }
        if (name === "download-template" && template) {
            const url = URL.createObjectURL(
                new Blob([template], { type: "application/json" }),
            );
            const link = document.createElement("a");
            link.href = url;
            link.download = "goblin-keycloak-realm.json";
            link.click();
            setTimeout(() => URL.revokeObjectURL(url), 1000);
            announce(
                "Realm import file saved. In Keycloak, choose Create realm and select this file.",
            );
            return;
        }
        void run(async () => {
            if (name === "package") {
                pluginPackage = undefined;
                announce("Building the plugin package…");
                pluginPackage = await api<ChatGptPackage>("/package", {
                    ...Object.fromEntries(drafts.get("listing") ?? []),
                    ...Object.fromEntries(drafts.get("review") ?? []),
                });
                announce(
                    "Package generated. Review its contents here, then save the ZIP to upload.",
                );
            } else if (name === "verify") {
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
            } else if (name === "revoke") {
                await api("/grants/revoke", { id: button.dataset.id });
                resetConnectionTests();
                announce(
                    "Access revoked. New reads will be denied; an in-progress read may finish.",
                );
                await refresh();
            } else if (name === "keys/refresh") {
                await api("/keys/refresh", {});
                announce(
                    "Signing-key cache cleared. The next authenticated request fetches the provider’s current keys.",
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
            event.target instanceof Element &&
            event.target.closest(
                'form[data-chatgpt-form="listing"], form[data-chatgpt-form="review"]',
            )
        )
            pluginPackage = undefined;
    }
    function submit(event: Event) {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) || !form.dataset.chatgptForm)
            return;
        event.preventDefault();
        if (busy) return;
        const data = new FormData(form);
        void run(async () => {
            if (
                form.dataset.chatgptForm === "listing" ||
                form.dataset.chatgptForm === "review"
            ) {
                pluginPackage = undefined;
                announce(
                    "Details kept for this setup session. Confirm the checklist and continue when ready.",
                );
            } else if (form.dataset.chatgptForm === "instance") {
                const origin = httpsAddress(String(data.get("origin") ?? ""));
                const issuer = httpsAddress(String(data.get("issuer") ?? ""));
                if (
                    origin.pathname !== "/" ||
                    !/^\/realms\/[^/.%\s]+$/.test(issuer.pathname)
                )
                    throw new Error(
                        "Use a Goblin origin without a path and an exact Keycloak issuer ending in /realms/your-realm, with no trailing slash.",
                    );
                config = `env:\n  - name: ${state!.environmentVariables.publicOrigin}\n    value: ${JSON.stringify(origin.origin)}\n  - name: ${state!.environmentVariables.issuer}\n    value: ${JSON.stringify(issuer.href)}`;
                announce(
                    "Settings generated. Apply them to your Goblin application container, then restart it.",
                );
            } else if (form.dataset.chatgptForm === "provider") {
                const profile = await api<unknown>("/provider-template", {
                    redirectUri: String(data.get("redirectUri") ?? "").trim(),
                });
                if (disposed) return;
                template = JSON.stringify(profile, null, 2);
                announce(
                    "Realm configuration generated. Preview it here, then save the import file for Keycloak.",
                );
            } else if (form.dataset.chatgptForm === "grant") {
                await api("/grants", {
                    subject: String(data.get("subject") ?? "").trim(),
                    workId: data.get("workId"),
                    hours: Number(data.get("hours")),
                    shareApprovedResult: data.has("shareApprovedResult"),
                });
                resetConnectionTests();
                announce(
                    "Work shared. Continue to test the new access in ChatGPT.",
                );
                form.reset();
                await refresh();
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
        throw new Error(
            "Enter complete HTTPS addresses for Goblin and Keycloak.",
        );
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

function saveFile(blob: Blob, name: string) {
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = name;
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
