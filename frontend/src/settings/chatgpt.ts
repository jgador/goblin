import { errorMessage, requestJson } from "../api/client.js";
import type {
    ChatGptState,
    ChatGptVerification,
} from "../api/chatgpt-contracts.js";
import { escapeHtml as e } from "../work/presentation.js";

const base = "/api/integrations/chatgpt";

export function mountChatGpt(root: HTMLElement) {
    const controller = new AbortController();
    let disposed = false;
    let busy = false;
    let state: ChatGptState | undefined;
    let work: { id: string; objective: string }[] = [];
    let verification: ChatGptVerification | undefined;
    let notice = "";
    const api = <T>(path = "", body?: unknown) =>
        requestJson<T>(base + path, {
            body,
            signal: controller.signal,
            onUnauthorized: () =>
                window.dispatchEvent(new Event("goblin-workspace-locked")),
        });

    function render() {
        if (disposed) return;
        const drafts = [
            ...root.querySelectorAll<HTMLFormElement>(
                "form[data-chatgpt-form]",
            ),
        ].map((form) => ({
            kind: form.dataset.chatgptForm,
            values: new FormData(form),
        }));
        root.innerHTML = `<h3>ChatGPT plugin</h3><p class="settings-description">Create your own plugin in ChatGPT and connect it to this Goblin instance. You choose which Work and approved results to share.</p><p class="settings-notice" role="status" data-chatgpt-notice>${e(notice)}</p>
            ${
                !state
                    ? '<p data-chatgpt-loading>Loading connection details…</p><button data-chatgpt="reload">Reload setup</button>'
                    : !state.configured
                      ? `<div class="chatgpt-settings"><h4>Prepare your instance</h4><p>Enable durable Work, configure HTTPS for Goblin, and set up a customer-owned Keycloak realm. Your administrator then sets <code>GOBLIN_CHATGPT_ISSUER</code> to its exact HTTPS realm address and restarts Goblin.</p><p>ChatGPT must allow custom MCP servers in your workspace. Its servers and your browser must be able to reach the OAuth provider.</p><p><a href="${base}/guide" download>Download the installation guide</a></p><button data-chatgpt="reload">Check configuration again</button></div>`
                      : `
            <div class="chatgpt-settings">
                <section><h4>1. Check prerequisites</h4><p>Use a healthy Goblin installation with HTTPS, Keycloak 26.8.1 with resource indicators enabled, and permission to add custom MCP servers in ChatGPT. A Secure MCP Tunnel can carry MCP traffic; OAuth still needs its own reachable HTTPS address.</p><button data-chatgpt="verify">Check provider and storage</button>
                ${verification ? `<ul class="chatgpt-checks"><li>${verification.discovery ? "✓ Provider discovery meets the configured requirements." : "Provider check failed. Check HTTPS, the realm issuer, and enabled scopes."}</li><li>${verification.database ? "✓ Work grant storage is available." : "Storage check failed. Check PostgreSQL and the current schema."}</li></ul><p class="settings-description">Checked ${e(new Date(verification.checkedAt).toLocaleString())}. Complete sign-in and a tool call in ChatGPT to verify the connection.</p>` : ""}</section>
                <section><h4>2. Prepare OAuth</h4><p>Your instance uses <strong>${e(state.issuer ?? "")}</strong>. Create a dedicated confidential client named <code>${e(state.clientId)}</code> with authorization code and required PKCE S256. Copy the exact callback shown by ChatGPT into the provider’s redirect allowlist.</p>
                <details><summary>Download a Keycloak realm template</summary><p>Import into a new realm on your own provider. The template contains no users or passwords. A blank callback creates a client that cannot sign in until you add the exact callback from ChatGPT.</p><form data-chatgpt-form="provider"><label>ChatGPT OAuth callback (optional initially)<input name="redirectUri" type="url" autocomplete="off" placeholder="Copy from ChatGPT connection settings"></label><button>Download realm template</button></form></details></section>
                <section><h4>3. Add and install in ChatGPT</h4><p>Open ChatGPT → Plugins → <strong>Add custom MCP server</strong>. Choose Server URL and OAuth, enter the details below, then <strong>Create as a plugin</strong> and install it. Sign in to your own OAuth provider.</p>
                ${field("Server URL", "serverUrl", state.serverUrl ?? "")}${field("Plugin name", "name", state.name)}${field("Description", "description", state.description)}${field("OAuth client ID", "clientId", state.clientId)}${field("Read scope", "scope", state.scope)}
                <p>Copy the client secret from Keycloak’s client Credentials tab directly into ChatGPT’s OAuth connection form. Goblin does not store this secret. Enter your password only on your provider’s sign-in page. <a href="${e(state.logoUrl)}" download="goblin.svg">Download Goblin logo</a> for surfaces that support it.</p></section>
                <section><h4>4. Share Work</h4><p>Copy the user’s immutable ID (<code>sub</code>) from your Keycloak administrator console. Signing in alone grants no Work access. Each grant includes the objective, status, and attention codes until it expires.</p>
                <form data-chatgpt-form="grant"><label>OAuth user ID<input name="subject" required maxlength="256" autocomplete="off"></label><label><span id="chatgpt-work-label">Work</span><select name="workId" aria-labelledby="chatgpt-work-label" required><option value="">Choose Work</option>${work.map((w) => `<option value="${e(w.id)}">${e(w.objective.slice(0, 120))} (${e(w.id)})</option>`).join("")}</select></label><label><span id="chatgpt-expiry-label">Access expires in</span><select name="hours" aria-labelledby="chatgpt-expiry-label"><option value="24">24 hours</option><option value="1">1 hour</option><option value="168">7 days</option></select></label><label class="chatgpt-checkbox"><input type="checkbox" name="shareApprovedResult">Also share the latest approved result</label><p class="settings-description">Sharing a result pins that approved attempt. Future results remain private. Re-sharing the same Work replaces its current grant.</p><button class="settings-primary" ${work.length ? "" : "disabled"}>Share selected Work</button></form>
                ${state.grants.length ? `<ul class="chatgpt-grants">${state.grants.map((g) => `<li><div><strong>Work ${e(g.workId)}</strong><span>${e(g.subject)}</span><small>${g.resultAttemptId ? `Approved result from attempt ${e(g.resultAttemptId)} included. ` : "Status and attention only. "}${g.expired ? "Expired" : "Expires"} ${e(new Date(g.expiresAt).toLocaleString())}</small></div><button data-chatgpt="revoke" data-id="${e(g.id)}" aria-label="Revoke Work ${e(g.workId)} for ${e(g.subject)}">Revoke</button></li>`).join("")}</ul>` : "<p>No Work is shared yet.</p>"}</section>
                <section><h4>5. Verify in ChatGPT</h4><p>Mention your installed plugin with <code>@</code> and ask “List the Work shared with this connection.” Check a shared status, then an unshared Work ID. The unshared read must be denied. Revoke a grant here and repeat its read to confirm access stops.</p><p>Refresh the plugin’s tools in ChatGPT after upgrading Goblin. Results appear only when explicitly shared above.</p></section>
                <details><summary>Troubleshooting and recovery</summary><p><strong>Cannot connect:</strong> check the server URL, TLS certificate, proxy Host forwarding, and ChatGPT workspace permissions. A tunnel does not make the OAuth provider reachable.</p><p><strong>Sign-in fails:</strong> verify the exact callback, PKCE S256, read scope, resource indicators, and access-token format using the installation guide.</p><p><strong>Empty list or access denied:</strong> check the OAuth user ID, grant expiry, and selected Work. Signing in again does not create grants.</p><p><strong>Changed signing keys:</strong> after retiring a compromised key at the provider, clear Goblin’s key cache. Requests already in progress may finish.</p><button data-chatgpt="keys/refresh">Clear signing-key cache</button><p><a href="${base}/guide" download>Download the complete installation guide</a></p></details>
            </div>`
            }`;
        for (const draft of drafts) {
            const form = root.querySelector<HTMLFormElement>(
                `form[data-chatgpt-form="${draft.kind}"]`,
            );
            if (!form) continue;
            for (const control of form.querySelectorAll<
                HTMLInputElement | HTMLSelectElement
            >("input, select")) {
                if (
                    control instanceof HTMLInputElement &&
                    control.type === "checkbox"
                )
                    control.checked = draft.values.has(control.name);
                else
                    control.value = String(
                        draft.values.get(control.name) ?? "",
                    );
            }
        }
    }

    function field(label: string, key: string, value: string) {
        return `<div class="chatgpt-copy"><label>${label}<input data-copy-value="${key}" readonly value="${e(value)}"></label><button data-chatgpt="copy" data-key="${key}" aria-label="Copy ${label.toLowerCase()}">Copy</button></div>`;
    }

    function announce(message: string) {
        if (disposed) return;
        notice = message;
        const element = root.querySelector<HTMLElement>(
            "[data-chatgpt-notice]",
        );
        if (element) element.textContent = message;
    }

    async function refresh() {
        const next = await api<ChatGptState>();
        const choices = next.configured ? await api<typeof work>("/work") : [];
        if (disposed) return;
        state = next;
        work = choices;
        render();
    }

    async function run(action: () => Promise<void>) {
        if (busy || disposed) return;
        busy = true;
        root.querySelectorAll<HTMLButtonElement>("button").forEach(
            (b) => (b.disabled = true),
        );
        try {
            await action();
        } catch (error) {
            announce(
                errorMessage(error, "ChatGPT setup could not be confirmed."),
            );
        } finally {
            busy = false;
            if (!disposed)
                root.querySelectorAll<HTMLButtonElement>("button").forEach(
                    (b) =>
                        (b.disabled =
                            b.closest('form[data-chatgpt-form="grant"]') !==
                                null && work.length === 0),
                );
        }
    }

    function click(event: Event) {
        if (!(event.target instanceof Element)) return;
        const button = event.target.closest<HTMLElement>("[data-chatgpt]");
        if (!button) return;
        void run(async () => {
            const action = button.dataset.chatgpt;
            if (action === "copy") {
                const input = root.querySelector<HTMLInputElement>(
                    `[data-copy-value="${button.dataset.key}"]`,
                );
                if (!input) return;
                try {
                    await navigator.clipboard.writeText(input.value);
                    announce("Copied.");
                } catch {
                    input.select();
                    announce("Select and copy the highlighted value.");
                }
            } else if (action === "verify") {
                verification = await api<ChatGptVerification>("/verify", {});
                render();
            } else if (action === "revoke") {
                await api("/grants/revoke", { id: button.dataset.id });
                notice =
                    "Access revoked. New reads will be denied; an in-progress read may finish.";
                await refresh();
            } else if (action === "keys/refresh") {
                await api("/keys/refresh", {});
                announce(
                    "Signing-key cache cleared. The next authenticated request fetches the provider’s current keys.",
                );
            } else if (action === "reload") await refresh();
        });
    }

    function submit(event: Event) {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) || !form.dataset.chatgptForm)
            return;
        event.preventDefault();
        const data = new FormData(form);
        void run(async () => {
            if (form.dataset.chatgptForm === "provider") {
                const profile = await api<unknown>("/provider-template", {
                    redirectUri: String(data.get("redirectUri") ?? "").trim(),
                });
                if (disposed) return;
                const url = URL.createObjectURL(
                    new Blob([JSON.stringify(profile, null, 2)], {
                        type: "application/json",
                    }),
                );
                const link = document.createElement("a");
                link.href = url;
                link.download = "goblin-keycloak-realm.json";
                link.click();
                setTimeout(() => URL.revokeObjectURL(url), 1000);
                announce(
                    "Realm template downloaded. Review it before importing into your provider.",
                );
            } else {
                await api("/grants", {
                    subject: String(data.get("subject") ?? "").trim(),
                    workId: data.get("workId"),
                    hours: Number(data.get("hours")),
                    shareApprovedResult: data.has("shareApprovedResult"),
                });
                notice =
                    "Work shared. Verify the new access with your installed plugin in ChatGPT.";
                form.reset();
                await refresh();
            }
        });
    }

    root.addEventListener("click", click);
    root.addEventListener("submit", submit);
    render();
    void run(refresh);
    return () => {
        disposed = true;
        controller.abort();
        root.removeEventListener("click", click);
        root.removeEventListener("submit", submit);
        root.replaceChildren();
    };
}
