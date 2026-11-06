import type {
    ChatGptState,
    ChatGptVerification,
} from "../api/chatgpt-contracts.js";
import { escapeHtml as e } from "../work/presentation.js";

export const setupSteps = ["Prepare", "Sign-in", "Install", "Test"] as const;
export const confirmations = [
    "installed",
    "listed",
    "written",
    "revoked",
] as const;
export type Confirmation = (typeof confirmations)[number];
export type Workflow = {
    state: ChatGptState;
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
    return `<h4 id="chatgpt-step-title" tabindex="-1">Prepare your instance first</h4><p class="chatgpt-callout">Prepare this Goblin instance first to get its connection details.</p>${action("prepare", "Go to Prepare", true)}`;
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
        c.has("listed") && c.has("written"),
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
        ${task("2. Check the server and storage", `${action("verify", "Check provider and storage", true)}${v ? `<ul class="chatgpt-checks">${check(v.discovery, "OAuth server configuration")}${check(v.database, "OAuth and Work storage")}${check(v.issuerResponses, "Authorization response issuer")}</ul><p class="settings-description">Checked ${e(new Date(v.checkedAt).toLocaleString())}. These are local Goblin checks. Verify HTTPS reachability and ChatGPT sign-in when installing.</p>` : "<p>Run this after creating the client. A passing check unlocks the next step.</p>"}`)}`;
}
function install(m: Workflow) {
    const s = m.state;
    return `<h4 id="chatgpt-step-title" tabindex="-1">Create and install your private plugin</h4><p>Keep Goblin and ChatGPT open in separate tabs. Enter Goblin's details in ChatGPT, save its return URL here, then sign in. This private connection can read and manage all existing and future Work.</p>
        ${task("1. Open the custom server form in ChatGPT", `${external("https://chatgpt.com/plugins", "Open ChatGPT Plugins")}<p>Select <strong>+ → Add custom MCP server</strong>, then copy the name and description below into that form.</p>${copyField("Plugin name", "name", s.name)}${copyField("Description", "description", s.description, true)}`)}
        ${task("2. Enter Goblin's connection details in ChatGPT", `<p>In ChatGPT's form, choose <strong>Server URL</strong> and <strong>OAuth</strong>, or your configured Secure MCP Tunnel. Copy the values below into the matching fields there and enter your saved client secret.</p><p>Choose <code>client_secret_basic</code> for <strong>Token endpoint auth method</strong>. If <strong>Base scopes</strong> is shown, use the OAuth scopes below.</p>${copyField("Server URL", "serverUrl", s.serverUrl!)}${copyField("OAuth client ID", "clientId", s.clientId)}${copyField("OAuth scopes", "scope", s.scope)}${clientSecret(m)}`)}
        ${callbackTask(m)}
        ${task("4. Return to ChatGPT and connect", `<div data-callback-blocked ${callbackSaved(m) ? "hidden" : ""}><p class="chatgpt-callout">Save the return URL in section 3 above before starting sign-in in ChatGPT. After Goblin shows <strong>Callback saved</strong>, you can continue here.</p></div><div data-callback-ready ${callbackSaved(m) ? "" : "hidden"}><p>Return to the form you left open in ChatGPT to create and install the plugin. When Goblin's HTTPS sign-in page opens, enter your Goblin password and approve full Work access. Goblin then sends you back to ChatGPT to finish.</p><p>If sign-in already failed before you saved the callback, restart the connection from ChatGPT to get a fresh request. Reconnect an older read-only plugin to grant write access.</p><details><summary>Check tools and try a first call</summary><p>Refresh the plugin in ChatGPT and confirm it shows <code>goblin_list_work</code>, <code>goblin_get_work</code>, <code>goblin_get_work_section</code>, <code>goblin_reserve_work_ids</code>, <code>goblin_create_work</code>, and <code>goblin_add_work_context</code>, plus tools for the other Work actions and available repository discovery.</p><p>Select your plugin with <strong>@</strong> in a new conversation and ask “Call goblin_ping now and show every field from the tool response unchanged.” Match its response ID to the goblin_ping event in Goblin's logs. ChatGPT may confirm writes according to your conversation and workspace settings.</p></details></div>${confirm(m, "installed", "I installed the plugin, signed in, and can see its tools in ChatGPT.", !callbackSaved(m))}`)}`;
}
function testConnection(m: Workflow) {
    return `<h4 id="chatgpt-step-title" tabindex="-1">Try your plugin in a conversation</h4><p>Open a new ChatGPT conversation, type <strong>@</strong>, and select your installed Goblin plugin.</p>${external("https://chatgpt.com/", "Open ChatGPT")}
        ${task("1. Read existing and new Work", `${copyField("Read prompt", "list-prompt", "List all Goblin Work. Show the full objective, status, questions, attempts, and results for one Work item created before this connection and one created after it. Use continuation until content is complete.", true)}<p>Compare both items with Goblin. No individual grant or result selection is needed.</p>${confirm(m, "listed", "Existing and new Work are readable without sharing either item.")}`)}
        ${task("2. Create and update Work", `${copyField("Write prompt", "write-prompt", "Reserve Work and command IDs, then create a Goblin Work item with the objective ‘Draft a short checklist for a weekend garden project.’ Read its version, reserve another command ID, add the context ‘Keep the checklist under five steps,’ then show its current status and messages.", true)}<p>Confirm the item and context appear in Goblin. ChatGPT may ask you to confirm write tool calls.</p>${confirm(m, "written", "Work created and updated through ChatGPT appears in Goblin.")}`)}
        ${task("3. Verify revocation when needed", `<p>Use <strong>Revoke ChatGPT connections</strong> below, then ask the same conversation to list Work and create another item. Both calls must be denied. Reconnect in ChatGPT to resume access.</p>${confirm(m, "revoked", "Revocation denied both reads and writes.")}`)}
        ${completed(m)[3] ? `<p class="chatgpt-callout"><strong>Your Work connection is ready.</strong> Keep using it with @ in ChatGPT. Goblin lifecycle rules still apply to execution, approval, and retry.</p>` : '<p class="settings-description">Check each result after testing. Goblin cannot observe your ChatGPT conversation.</p>'}`;
}
