import type {
    ChatGptState,
    ChatGptVerification,
} from "../api/chatgpt-contracts.js";
import { escapeHtml as e } from "../work/presentation.js";

export const setupSteps = [
    "Prepare",
    "Sign-in",
    "Install",
    "Share Work",
    "Test",
] as const;
export const confirmations = [
    "installed",
    "listed",
    "status",
    "denied",
] as const;
export type Confirmation = (typeof confirmations)[number];
export type Workflow = {
    state: ChatGptState;
    work: { id: string; objective: string }[];
    verification?: ChatGptVerification;
    confirmed: Set<Confirmation>;
    step: number;
    config: string;
    clientSecret: string;
    callbackValue?: string;
};

export function action(
    name: string,
    label: string,
    primary = false,
    disabled = false,
) {
    return `<button type="button" id="chatgpt-${name}" data-chatgpt="${name}" class="${primary ? "settings-primary" : ""}" ${disabled ? "disabled" : ""}>${label}</button>`;
}
function external(url: string, label: string) {
    return `<a class="chatgpt-external" href="${e(url)}" target="_blank" rel="noopener noreferrer">${label} <span aria-hidden="true">↗</span><span class="sr-only"> (opens in a new tab)</span></a>`;
}
export function copyField(
    label: string,
    key: string,
    value: string,
    multiline = false,
) {
    return `<div class="chatgpt-copy"><label for="chatgpt-value-${key}"><span id="chatgpt-label-${key}">${label}</span>${multiline ? `<textarea id="chatgpt-value-${key}" aria-labelledby="chatgpt-label-${key}" data-copy-value="${key}" readonly rows="${key === "config" ? 6 : 3}">${e(value)}</textarea>` : `<input id="chatgpt-value-${key}" aria-labelledby="chatgpt-label-${key}" data-copy-value="${key}" readonly value="${e(value)}">`}</label><button type="button" id="chatgpt-copy-${key}" data-chatgpt="copy" data-key="${key}" aria-label="Copy ${e(label.toLowerCase())}">Copy</button></div>`;
}
function confirm(
    model: Workflow,
    key: Confirmation,
    label: string,
    disabled = false,
) {
    return `<label class="chatgpt-checkbox"><input id="chatgpt-confirm-${key}" type="checkbox" data-confirm="${key}" ${model.confirmed.has(key) && !disabled ? "checked" : ""} ${disabled ? "disabled" : ""}>${label}</label>`;
}
function task(title: string, body: string) {
    const [number, ...label] = title.split(". ");
    return `<div class="chatgpt-task"><h5><span class="chatgpt-task-number">${number}</span>${label.join(". ")}</h5>${body}</div>`;
}
function unavailable() {
    return `<h4 id="chatgpt-step-title" tabindex="-1">Prepare your instance first</h4><p class="chatgpt-callout">Prepare this Goblin instance first to get its connection details and enable sharing.</p>${action("prepare", "Go to Prepare", true)}`;
}
export function providerReady(model: Workflow) {
    return !!(
        model.state.clientConfigured &&
        model.verification?.discovery &&
        model.verification.database &&
        model.verification.issuerResponses
    );
}
export function callbackSaved(model: Workflow) {
    const saved = model.state.redirectUri;
    return !!(
        model.state.clientConfigured &&
        saved &&
        (model.callbackValue ?? saved).trim() === saved
    );
}
export function callbackFeedback(model: Workflow) {
    if (callbackSaved(model))
        return "Callback saved. You can return to ChatGPT and sign in.";
    if (model.state.redirectUri)
        return "Unsaved changes. Click Save callback before returning to ChatGPT.";
    if (model.callbackValue?.trim())
        return "Callback entered but not saved. Click Save callback to continue.";
    return "Callback not saved. Paste ChatGPT's callback, then click Save callback.";
}
export function completed(model: Workflow): boolean[] {
    const c = model.confirmed;
    return [
        model.state.configured,
        providerReady(model),
        callbackSaved(model) && c.has("installed"),
        model.state.grants.some((g) => !g.expired),
        c.has("listed") && c.has("status") && c.has("denied"),
    ];
}
export function stepContent(model: Workflow): string {
    if (model.step > 0 && !model.state.configured) return unavailable();
    switch (model.step) {
        case 0:
            return prepare(model);
        case 1:
            return provider(model);
        case 2:
            return install(model);
        case 3:
            return share(model);
        default:
            return testConnection(model);
    }
}
function prepare(m: Workflow) {
    return `<h4 id="chatgpt-step-title" tabindex="-1">Prepare your instance</h4><p>Connect ChatGPT to your own Goblin using its HTTPS address and your existing Goblin password.</p>
        ${
            m.state.configured
                ? `<p class="chatgpt-callout">Goblin's OAuth server and MCP endpoint are enabled. Next, prepare your ChatGPT connection.</p>${copyField("Server URL", "serverUrl", m.state.serverUrl!)}${copyField("OAuth issuer", "issuer", m.state.issuer!)}`
                : `
        ${task("1. Get your HTTPS address", "<p>Deploy Goblin with durable Work, PostgreSQL, persistent storage, and a reachable HTTPS address. Goblin includes its own sign-in server.</p><p>A localhost address alone cannot be used by ChatGPT. A Secure MCP Tunnel can carry MCP traffic; Goblin's OAuth endpoints still need reachable HTTPS.</p>")}
        ${task("2. Generate your installation settings", `<form data-chatgpt-form="instance"><label>Public Goblin origin<input name="origin" type="url" required placeholder="https://app.example.com" autocomplete="off"></label><button class="settings-primary">Generate settings</button></form>${m.config ? `${copyField("Container environment settings", "config", m.config, true)}<p>Apply these entries to Goblin's container, retaining its database, password, and persistent storage settings. Restart Goblin, then open settings at its HTTPS address.</p>` : "<p>Enter your Goblin address to get installation settings. Azure installations configure HTTPS and the sign-in server automatically.</p>"}<p>For local development through an Azure relay, open this page at the relay’s HTTPS address. That page and ChatGPT must reach the same Goblin instance.</p>`)}
        ${task("3. Apply settings and restart Goblin", `<p>After restarting, check again to continue.</p>${action("reload", "Check configuration again")}`)}`
        }
        <details><summary>Using a private network?</summary><p>A Secure MCP Tunnel must preserve Goblin's canonical Host. OAuth discovery, sign-in, token, and signing-key endpoints must remain reachable over HTTPS.</p>${external("https://developers.openai.com/api/docs/guides/secure-mcp-tunnels", "Secure MCP Tunnel setup")}</details>`;
}
function clientSecret(m: Workflow) {
    return m.clientSecret
        ? `${copyField("OAuth client secret", "clientSecret", m.clientSecret)}<p>Save this secret and paste it into ChatGPT's OAuth form during Install. It is shown only during this setup session.</p>`
        : m.state.clientConfigured
          ? "<p>Use the client secret you saved when creating this client. Goblin cannot display it again.</p>"
          : "";
}
function clientPreparation(m: Workflow) {
    return `<p>Create the OAuth client and save its secret here. ChatGPT supplies its callback during <strong>Install</strong>; you will save it there before sign-in.</p>${
        m.state.clientConfigured
            ? `<p class="chatgpt-callout">Your OAuth client is already configured. Keep its existing secret to reuse the connection.</p><details><summary>Need a replacement client secret?</summary><form data-chatgpt-form="client"><label class="chatgpt-checkbox"><input type="checkbox" name="rotateSecret" required>Generate a new client secret and revoke previous connections</label><button>Generate new client secret</button></form></details>`
            : '<form data-chatgpt-form="client"><button>Create OAuth client</button></form>'
    }${clientSecret(m)}`;
}
function callbackForm(m: Workflow) {
    if (!m.state.clientConfigured)
        return `<p class="chatgpt-callout">Create your OAuth client in Sign-in first, then return here to save ChatGPT's callback.</p>${action("provider", "Go to Sign-in", true)}`;
    return `<form data-chatgpt-form="callback"><label for="chatgpt-callback-url">ChatGPT OAuth callback (return URL)<input id="chatgpt-callback-url" name="redirectUri" type="url" required maxlength="512" value="${e(m.callbackValue ?? m.state.redirectUri ?? "")}" aria-describedby="chatgpt-callback-purpose chatgpt-callback-help chatgpt-callback-status" autocomplete="off" placeholder="https://chatgpt.com/…"></label><p id="chatgpt-callback-help" class="settings-description">Use the exact URL shown by ChatGPT. Entering it does not save it; click <strong>Save callback</strong> below. Wildcards are rejected.</p><button id="chatgpt-save-callback" type="submit" class="settings-primary">Save callback</button><p id="chatgpt-callback-status" class="chatgpt-callback-status" role="status">${callbackFeedback(m)}</p></form>`;
}
function callbackTask(m: Workflow) {
    return `<div class="chatgpt-task chatgpt-callback-task" data-callback-saved="${callbackSaved(m)}"><p class="chatgpt-callback-label" data-callback-label>${callbackSaved(m) ? "Callback saved" : "Required in Goblin"}</p><h5><span class="chatgpt-task-number">3</span>Save ChatGPT's return URL in Goblin</h5><p id="chatgpt-callback-purpose">The OAuth callback is the ChatGPT address Goblin sends you back to after you sign in. Goblin must allow this address before ChatGPT can connect.</p><ol class="chatgpt-callback-instructions"><li><strong>In ChatGPT:</strong> copy the <strong>OAuth callback</strong> URL displayed in the custom MCP server form after entering your connection details. Keep that form open.</li><li><strong>Here in Goblin:</strong> paste that URL below, click <strong>Save callback</strong>, and wait for <strong>Callback saved</strong>.</li></ol>${callbackForm(m)}</div>`;
}
function provider(m: Workflow) {
    const v = m.verification;
    const check = (ok: boolean, title: string) =>
        `<li data-passed="${ok}"><strong>${ok ? "Passed" : "Needs attention"}: ${title}</strong></li>`;
    return `<h4 id="chatgpt-step-title" tabindex="-1">Set up secure sign-in</h4><p>ChatGPT connects as the Goblin owner. You will unlock Goblin and approve the connection in your browser.</p>
        ${task("1. Prepare the OAuth client", clientPreparation(m))}
        ${task("2. Check the server and storage", `${action("verify", "Check provider and storage", true)}${v ? `<ul class="chatgpt-checks">${check(v.discovery, "OAuth server configuration")}${check(v.database, "OAuth and Work grant storage")}${check(v.issuerResponses, "Authorization response issuer")}</ul><p class="settings-description">Checked ${e(new Date(v.checkedAt).toLocaleString())}. These are local Goblin checks. Verify HTTPS reachability and ChatGPT sign-in when installing.</p>` : "<p>Run this after creating the client. A passing check unlocks the next step.</p>"}`)}`;
}
function install(m: Workflow) {
    const s = m.state;
    return `<h4 id="chatgpt-step-title" tabindex="-1">Create and install your private plugin</h4><p>Keep Goblin and ChatGPT open in separate tabs. Enter Goblin's details in ChatGPT, save its return URL here, then sign in. Your private plugin reads only the Work you choose to share.</p>
        ${task("1. Open the custom server form in ChatGPT", `${external("https://chatgpt.com/plugins", "Open ChatGPT Plugins")}<p>Select <strong>+ → Add custom MCP server</strong>, then copy the name and description below into that form.</p>${copyField("Plugin name", "name", s.name)}${copyField("Description", "description", s.description, true)}`)}
        ${task("2. Enter Goblin's connection details in ChatGPT", `<p>In ChatGPT's form, choose <strong>Server URL</strong> and <strong>OAuth</strong>, or your configured Secure MCP Tunnel. Copy the values below into the matching fields there and enter your saved client secret.</p><p>Choose <code>client_secret_basic</code> for <strong>Token endpoint auth method</strong>. If <strong>Base scopes</strong> is shown, use the OAuth scopes below.</p>${copyField("Server URL", "serverUrl", s.serverUrl!)}${copyField("OAuth client ID", "clientId", s.clientId)}${copyField("OAuth scopes", "scope", s.scope)}${clientSecret(m)}`)}
        ${callbackTask(m)}
        ${task("4. Return to ChatGPT and connect", `<div data-callback-blocked ${callbackSaved(m) ? "hidden" : ""}><p class="chatgpt-callout">Save the return URL in section 3 above before starting sign-in in ChatGPT. After Goblin shows <strong>Callback saved</strong>, you can continue here.</p></div><div data-callback-ready ${callbackSaved(m) ? "" : "hidden"}><p>Return to the form you left open in ChatGPT to create and install the plugin. When Goblin's HTTPS sign-in page opens, enter your Goblin password and approve the connection. Goblin then sends you back to ChatGPT to finish.</p><p>If sign-in already failed before you saved the callback, restart the connection from ChatGPT to get a fresh request.</p><details><summary>Check tools and try a first call</summary><p>Confirm these tools appear: <code>goblin_ping</code>, <code>goblin_list_work</code>, <code>goblin_get_work_status</code>, <code>goblin_get_work_attention</code>, and <code>goblin_get_work_result</code>.</p><p>To test the connection before sharing Work, select your plugin with <strong>@</strong> in a new ChatGPT conversation and ask “Call goblin_ping now and show every field from the tool response unchanged.” The reply includes a fresh response ID, UTC time, and basic server runtime details. Match the response ID to the goblin_ping response event in Goblin's logs to verify the call. No Work grants or GitHub sign-in are needed.</p></details></div>${confirm(m, "installed", "I installed the plugin, signed in, and can see its tools in ChatGPT.", !callbackSaved(m))}`)}`;
}

function share(m: Workflow) {
    const s = m.state;
    return `<h4 id="chatgpt-step-title" tabindex="-1">Choose what ChatGPT can read</h4><p>Signing in grants no Work access. Share only the Work you want your ChatGPT connection to read.</p>
        ${copyField("Owner OAuth ID", "ownerSubject", s.ownerSubject!)}
        <form data-chatgpt-form="grant"><label><span id="chatgpt-work-label">Work</span><select name="workId" aria-labelledby="chatgpt-work-label" required><option value="">Choose Work</option>${m.work.map((w) => `<option value="${e(w.id)}">${e(w.objective.slice(0, 120))} (${e(w.id)})</option>`).join("")}</select></label><label><span id="chatgpt-expiry-label">Access expires in</span><select name="hours" aria-labelledby="chatgpt-expiry-label"><option value="24">24 hours</option><option value="1">1 hour</option><option value="168">7 days</option></select></label><label class="chatgpt-checkbox"><input type="checkbox" name="shareApprovedResult">Also share the latest approved result</label><p class="settings-description">Shares the objective, status, and attention codes. Including a result pins its approved attempt; later results remain private. Re-sharing this Work replaces its grant.</p><button class="settings-primary" ${m.work.length ? "" : "disabled"}>Share selected Work</button></form>
        ${m.work.length ? "" : '<p class="chatgpt-callout">No Work is available to share. <a href="/">Create Work in Goblin</a>, then return here.</p>'}${action("reload", "Refresh Work and access")}
        ${s.grants.length ? `<ul class="chatgpt-grants">${s.grants.map((g) => `<li><div><strong>Work ${e(g.workId)}</strong><span>${e(g.subject)}</span><small>${g.resultAttemptId ? `Approved result from attempt ${e(g.resultAttemptId)} included. ` : "Status and attention only. "}${g.expired ? "Expired" : "Expires"} ${e(new Date(g.expiresAt).toLocaleString())}</small></div><button type="button" data-chatgpt="revoke" data-id="${e(g.id)}" aria-label="Revoke Work ${e(g.workId)} for ${e(g.subject)}">Revoke</button></li>`).join("")}</ul>` : "<p>No Work is shared yet.</p>"}`;
}
function testConnection(m: Workflow) {
    return `<h4 id="chatgpt-step-title" tabindex="-1">Try your plugin in a conversation</h4><p>Open a new ChatGPT conversation, type <strong>@</strong>, and select your installed Goblin plugin. Run these checks using the account you granted access to.</p>${external("https://chatgpt.com/", "Open ChatGPT")}
        ${task("1. List your shared Work", `${copyField("List prompt", "list-prompt", "List the Work shared with this connection.", true)}<p>Expect only this user’s unexpired Work grants. An empty list usually means the user ID or grant needs checking.</p>${confirm(m, "listed", "The list contains only Work I shared with this user.")}`)}
        ${task("2. Compare its status with Goblin", `${copyField("Status prompt", "status-prompt", "Show the status and attention details for the first Work item in that list.", true)}<p>Compare the answer with that Work in Goblin. An approved result is readable only if you explicitly included it in the grant.</p>${confirm(m, "status", "The status and attention details match Goblin.")}`)}
        ${task("3. Check that private Work stays private", `<p>Copy the ID of a Work item you did <strong>not</strong> share with this user, then ask ChatGPT for its status. Expect an access denial with no private details.</p>${confirm(m, "denied", "Reading an unshared Work ID was denied.")}`)}
        ${completed(m)[4] ? `<p class="chatgpt-callout"><strong>You confirmed the private plugin works.</strong> Keep using it with @ in ChatGPT. Return to Share Work to change or revoke access.</p>${action("share", "Manage shared Work")}` : '<p class="settings-description">Check each result after testing. Goblin cannot observe your ChatGPT conversation.</p>'}`;
}
