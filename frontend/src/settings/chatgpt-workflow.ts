import type {
    ChatGptState,
    ChatGptVerification,
    ChatGptPackage,
} from "../api/chatgpt-contracts.js";
import { escapeHtml as e } from "../work/presentation.js";

export const privateSteps = [
    "Prepare",
    "Sign-in",
    "Install",
    "Share Work",
    "Test",
] as const;
export const publicSteps = [
    "Prepare listing",
    "Review tests",
    "Submit",
    "Publish",
] as const;
export type SetupPath = "private" | "public";
export const confirmations = [
    "installed",
    "listed",
    "status",
    "denied",
    "listing",
    "review",
    "submitted",
    "published",
] as const;
export type Confirmation = (typeof confirmations)[number];
export type Workflow = {
    state: ChatGptState;
    work: { id: string; objective: string }[];
    verification?: ChatGptVerification;
    confirmed: Set<Confirmation>;
    path: SetupPath;
    step: number;
    config: string;
    clientSecret: string;
    package?: ChatGptPackage;
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
function confirm(model: Workflow, key: Confirmation, label: string) {
    return `<label class="chatgpt-checkbox"><input id="chatgpt-confirm-${key}" type="checkbox" data-confirm="${key}" ${model.confirmed.has(key) ? "checked" : ""}>${label}</label>`;
}
function task(title: string, body: string) {
    return `<div class="chatgpt-task"><h5>${title}</h5>${body}</div>`;
}
function input(
    label: string,
    name: string,
    value = "",
    type = "text",
    max = 1024,
) {
    return `<label>${label}<input name="${name}" type="${type}" value="${e(value)}" required maxlength="${max}" ${type === "url" ? 'pattern="https://.*" title="Use an HTTPS URL"' : ""} autocomplete="off"></label>`;
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
export function completed(model: Workflow): boolean[] {
    const c = model.confirmed;
    return model.path === "private"
        ? [
              model.state.configured,
              providerReady(model),
              c.has("installed"),
              model.state.grants.some((g) => !g.expired),
              c.has("listed") && c.has("status") && c.has("denied"),
          ]
        : [
              c.has("listing"),
              c.has("review"),
              c.has("submitted"),
              c.has("published"),
          ];
}
export function stepContent(model: Workflow): string {
    if (model.path === "public") return publication(model);
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
        ${task("2. Generate your installation settings", `<form data-chatgpt-form="instance"><label>Public Goblin origin<input name="origin" type="url" required placeholder="https://app.example.com" autocomplete="off"></label><button class="settings-primary">Generate settings</button></form>${m.config ? `${copyField("Container environment settings", "config", m.config, true)}<p>Apply these entries to Goblin's container, retaining its database, password, and persistent storage settings. Restart Goblin, then open settings at its HTTPS address.</p>` : "<p>Enter your Goblin address to get installation settings.</p>"}`)}
        ${task("3. Apply settings and restart Goblin", `<p>After restarting, check again to continue.</p>${action("reload", "Check configuration again")}`)}`
        }
        <details><summary>Using a private network?</summary><p>A Secure MCP Tunnel must preserve Goblin's canonical Host. OAuth discovery, sign-in, token, and signing-key endpoints must remain reachable over HTTPS.</p>${external("https://developers.openai.com/api/docs/guides/secure-mcp-tunnels", "Secure MCP Tunnel setup")}</details>`;
}
function clientForm(m: Workflow) {
    return `<form data-chatgpt-form="client"><label>ChatGPT OAuth callback<input name="redirectUri" type="url" maxlength="512" value="${e(m.state.redirectUri ?? "")}" autocomplete="off" placeholder="Copy the exact callback from ChatGPT"></label><p class="settings-description">You can create the client before ChatGPT displays a callback. Save the exact callback before completing sign-in.</p>${m.state.clientConfigured ? '<label class="chatgpt-checkbox"><input type="checkbox" name="rotateSecret">Generate a new client secret and revoke previous connections</label>' : ""}<button>${m.state.clientConfigured ? "Save OAuth client" : "Create OAuth client"}</button></form>${m.clientSecret ? `${copyField("OAuth client secret", "clientSecret", m.clientSecret)}<p>Copy this secret into ChatGPT. It is shown only during this setup session.</p>` : m.state.clientConfigured ? "<p>The saved secret cannot be displayed again. Generate a new secret if you need one.</p>" : ""}`;
}
function provider(m: Workflow) {
    const v = m.verification;
    const check = (ok: boolean, title: string) =>
        `<li><strong>${ok ? "Passed" : "Needs attention"}: ${title}</strong></li>`;
    return `<h4 id="chatgpt-step-title" tabindex="-1">Set up secure sign-in</h4><p>ChatGPT connects as the Goblin owner. You will unlock Goblin and approve the connection in your browser.</p>
        ${task("1. Prepare the OAuth client", clientForm(m))}
        ${task("2. Check the server and storage", `${action("verify", "Check provider and storage", true)}${v ? `<ul class="chatgpt-checks">${check(v.discovery, "OAuth server configuration")}${check(v.database, "OAuth and Work grant storage")}${check(v.issuerResponses, "Authorization response issuer")}</ul><p class="settings-description">Checked ${e(new Date(v.checkedAt).toLocaleString())}. These are local Goblin checks. Verify HTTPS reachability and ChatGPT sign-in when installing.</p>` : "<p>Run this after creating the client. A passing check unlocks the next step.</p>"}`)}`;
}
function install(m: Workflow) {
    const s = m.state;
    return `<h4 id="chatgpt-step-title" tabindex="-1">Create and install your private plugin</h4><p>Keep Goblin open while you complete the connection in ChatGPT.</p>
        ${task("1. Open the custom server form", `${external("https://chatgpt.com/plugins", "Open ChatGPT Plugins")}<p>Select <strong>+ → Add custom MCP server</strong>.</p>${copyField("Plugin name", "name", s.name)}${copyField("Description", "description", s.description, true)}`)}
        ${task("2. Paste your connection details", `<p>Choose <strong>Server URL</strong> and <strong>OAuth</strong>, or your configured Secure MCP Tunnel.</p>${copyField("Server URL", "serverUrl", s.serverUrl!)}${copyField("OAuth client ID", "clientId", s.clientId)}${copyField("OAuth scopes", "scope", s.scope)}<p>Paste the client secret generated in Goblin into ChatGPT's OAuth form.</p>`)}
        ${task("3. Save the callback and install", `<p>Copy the exact OAuth callback displayed by ChatGPT and save it here. Wildcard callbacks are rejected.</p>${clientForm(m)}<p>Return to ChatGPT, create and install the plugin, then enter your Goblin password on Goblin's HTTPS sign-in page and approve the connection.</p><p>Confirm all four tools appear: <code>goblin_list_work</code>, <code>goblin_get_work_status</code>, <code>goblin_get_work_attention</code>, and <code>goblin_get_work_result</code>.</p>${confirm(m, "installed", "I installed the plugin, signed in, and can see its four tools in ChatGPT.")}`)}`;
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
        ${completed(m)[4] ? `<p class="chatgpt-callout"><strong>You confirmed the private plugin works.</strong> Keep using it with @ in ChatGPT. Return to Share Work to change or revoke access.</p>${action("share", "Manage shared Work")}${action("public", "Continue to public submission")}` : '<p class="settings-description">Check each result after testing. Goblin cannot observe your ChatGPT conversation.</p>'}`;
}
function publication(m: Workflow) {
    switch (m.step) {
        case 0:
            return `<h4 id="chatgpt-step-title" tabindex="-1">Prepare your public listing</h4><p>Public publication makes a plugin discoverable in the directory. Your private plugin can be used without submitting a public listing.</p>
            ${task("1. Choose the service you will publish", `<p>Use one stable, publicly reachable HTTPS MCP endpoint. A localhost address or Secure MCP Tunnel is not eligible for public submission. Customer-specific template URLs require an established OpenAI relationship.</p>${m.state.serverUrl ? copyField("Current MCP endpoint", "publication-server", m.state.serverUrl) : "<p>Prepare your Goblin instance first to get its MCP endpoint.</p>"}<p>A configured URL does not establish public reachability or publication eligibility. Test the review service outside your private network.</p>`)}
            ${task("2. Verify your publisher and prepare listing details", `${external("https://platform.openai.com/settings/organization/general", "Open publisher verification")}<p>Verify the individual or business name you will publish under. Your Platform organization also needs permission to create and submit plugins.</p>${inputForm(m)}<p>These details become your uploadable plugin package. Goblin includes its logo and the Developer Tools category. Choose country availability in the submission portal. Draft fields stay on this page until you leave or reload.</p>${external(m.state.logoUrl, "View Goblin logo")}<p>Use the logo only where supported by the submission form.</p>`)}
            ${confirm(m, "listing", "I have a public review endpoint, verified publisher, artwork, and the required listing pages.")}`;
        case 1:
            return `<h4 id="chatgpt-step-title" tabindex="-1">Prepare the reviewer experience</h4><p>Use a dedicated Goblin review instance with neutral sample Work. Run each scenario in ChatGPT and record the prompt, tool, and actual response for the submission form.</p>
            ${task("1. Make the review instance ready", `<p>Reviewers must be able to sign in without a private network, MFA, email or SMS challenge, or further configuration. Goblin has one owner password, which also controls administration. Use a disposable password on an isolated review instance. Give its owner an unexpired Work grant and a separately shared approved result. Keep another Work item unshared for the denial check.</p><p>Enter reviewer credentials only in the submission portal’s secure form.</p>`)}
            ${task("2. Run five positive and three negative tests", `<ol class="chatgpt-review-tests"><li><strong>List shared Work.</strong> Ask “List Work shared with this connection.” Expect <code>goblin_list_work</code> and only the demo user’s shared items.</li><li><strong>Read status.</strong> Ask for a shared Work ID’s status. Expect <code>goblin_get_work_status</code> and the current summary.</li><li><strong>Read attention.</strong> Ask what needs attention on that Work. Expect <code>goblin_get_work_attention</code> and its attention codes.</li><li><strong>Read a shared result.</strong> Ask for the explicitly shared approved result. Expect <code>goblin_get_work_result</code> and the pinned approved attempt.</li><li><strong>Follow up.</strong> Ask “What is its status now?” after a shared Work response. Expect the status tool to reuse that Work ID.</li><li><strong>Unshared Work.</strong> Request the status of your unshared sample ID. Expect denial without private fields.</li><li><strong>Unshared result.</strong> Request a result on Work shared for status only. Expect denial.</li><li><strong>Unsupported action.</strong> Ask to start or retry Work. Expect an explanation that this must be done in Goblin, with no write action.</li></ol><p>Use exact sample IDs and observed responses when entering these cases. Initial MCP review requires exactly five positive and three negative cases.</p>`)}
            ${task("3. Record a walkthrough", `<p>Record sign-in, tool discovery, a shared read, and a denied read. Make the video accessible to reviewers. Check that your demo grants will remain valid during review; renew them as needed.</p>`)}
            <form data-chatgpt-form="review">${input("Status-only sample Work ID", "statusWorkId")}${input("Shared-result sample Work ID", "resultWorkId")}${input("Unshared sample Work ID", "privateWorkId")}${input("Demo video URL", "videoUrl", "", "url")}<button>Save review details</button></form><p>Use three distinct Work IDs. Keep their grants and sample data consistent with the cases above.</p>
            ${confirm(m, "review", "I ran all eight cases, recorded their results, and prepared an accessible demo video and account.")}`;
        case 2:
            return `<h4 id="chatgpt-step-title" tabindex="-1">Apply for plugin review</h4><p>Finish the submission in OpenAI’s portal. Goblin keeps this checklist so you can return to the next step.</p>
            ${task("1. Build your upload package", `<p>Use the listing and demo details you entered in the previous steps to generate an initial <strong>1.0.0</strong> plugin ZIP. It contains the manifest, OAuth connection settings, eight review cases, and a square Goblin logo.</p>${m.state.configured ? action("package", "Generate plugin package", true) : unavailable()}${m.package ? `<details><summary>Review package contents</summary><h5>plugin.json</h5><pre class="chatgpt-template">${e(m.package.pluginJson)}</pre><h5>mcp.json</h5><pre class="chatgpt-template">${e(m.package.mcpJson)}</pre></details>${action("download-package", "Save plugin ZIP")}<p>Review the generated prompts and expected results against your actual tests before submitting. Credentials are entered separately in OpenAI’s portal.</p>` : "<p>If you returned after a reload, re-enter listing and review details to regenerate the package, or upload the ZIP you already saved.</p>"}`)}
            ${task("2. Upload and connect in the portal", `${external("https://platform.openai.com/plugins", "Open plugin submission portal")}<p>Select <strong>Upload new or existing plugin</strong>, choose your verified developer identity, and upload the ZIP. Open <strong>MCPs → Connect</strong>. Complete the domain verification challenge, enter your OAuth client secret, and allow the exact callback shown there in Goblin settings before signing in.</p><p>Host the exact verification token as plain text at the challenge URL shown by the portal. Your hosting administrator must configure this on the public domain.</p><p>Wait for the tool scan and confirm all four Goblin tools. Resolve connection and metadata issues before proceeding.</p>`)}
            ${task("3. Complete and submit the application", `<p>Open <strong>Metadata &amp; Skills → Review information → Review details</strong>. Check the imported cases and video, enter private reviewer credentials, and save. Confirm availability countries and required policy attestations, then select <strong>Submit for review</strong>.</p><p>Track <strong>Review status</strong> in the portal. If changes are requested, edit the package or server, retest, and upload a corrected version. Imported review cases are edited in the package.</p>${external("https://developers.openai.com/plugins/deploy/submission", "Official submission requirements")}`)}
            ${confirm(m, "submitted", "The portal confirms I submitted this plugin for review.")}`;
        default:
            return `<h4 id="chatgpt-step-title" tabindex="-1">Publish and use the approved plugin</h4><p>Approval and publication are separate steps. Check your actual review status in OpenAI’s portal.</p>
            ${task("1. Publish after approval", `${external("https://platform.openai.com/plugins", "Open review status and publication")}<p>Once approved, select <strong>Publish plugin</strong>. Copy the directory link from the portal. Goblin cannot submit, approve, or publish on your behalf from this page.</p>`)}
            ${task("2. Install the published listing", `${external("https://chatgpt.com/plugins", "Open ChatGPT Plugins")}<p>Open your directory link or search for the exact publication name. Install the plugin, connect the intended account, then start a conversation and select it with <strong>@</strong>. Repeat the shared Work and denied-access checks.</p><p>Each connecting user still needs a Work grant in Goblin. Public listing visibility does not grant access to your Work.</p>`)}
            ${confirm(m, "published", "I published the approved plugin and tested an installation from its directory listing.")}
            ${m.confirmed.has("published") ? '<p class="chatgpt-callout"><strong>Publication recorded by you.</strong> Manage review status and future versions in the submission portal.</p>' : ""}`;
    }
}

function inputForm(m: Workflow) {
    return `<form data-chatgpt-form="listing">${input("Plugin ID", "packageId", "goblin", "text", 64)}${input("Listing name", "name", m.state.name, "text", 30)}${input("Verified publisher name", "publisher", "", "text", 80)}${input("Short description", "subtitle", "Read your shared Work", "text", 30)}<label>Listing description<textarea name="description" aria-label="Listing description" required maxlength="4000" rows="3">${e(m.state.description)}</textarea></label>${input("Website URL", "websiteUrl", "", "url")}${input("Support URL", "supportUrl", "", "url")}${input("Privacy policy URL", "privacyUrl", "", "url")}${input("Terms of service URL", "termsUrl", "", "url")}<button>Save listing details</button></form>`;
}
