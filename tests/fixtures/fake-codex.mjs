import { createInterface } from "node:readline";
import { readFile, writeFile, appendFile, unlink } from "node:fs/promises";
import { join } from "node:path";
const scenario = process.argv[2];
const root = process.env.CODEX_HOME;
if (!root)
    throw new Error("The fixture requires a private CODEX_HOME.");
const accountFile = join(root, "auth.json");
const send = (message) => {
    const bytes = Buffer.from(`${JSON.stringify(message)}\n`);
    if (scenario === "fragmented") {
        for (const byte of bytes) process.stdout.write(Buffer.from([byte]));
    } else process.stdout.write(bytes);
};
const result = (id, value) => send({ id, result: value });
let initialized = false;
let acknowledged = false;
let login = null;
let timer;
let promptTimer;
let threadNumber = 0;
let reordered = [];
await writeFile(join(root, "pid"), String(process.pid));
await writeFile(join(root, "arguments.json"), JSON.stringify(process.argv.slice(3)));
if (scenario === "stubborn") process.on("SIGTERM", () => {});
await writeFile(join(root, "environment.json"), JSON.stringify(process.env));
async function finishLogin() {
    if (!login)
        return;
    const loginId = login;
    login = null;
    await writeFile(accountFile, JSON.stringify({ type: "chatgpt", email: "owner@example.test", planType: "plus" }), { mode: 0o600 });
    send({ method: "account/login/completed", params: { loginId, success: true } });
    send({ method: "account/updated", params: { authMode: "chatgpt" } });
}
for await (const line of createInterface({ input: process.stdin })) {
    const { id, method, params } = JSON.parse(line);
    if (!method) {
        await appendFile(join(root, "server-responses.jsonl"), `${line}\n`);
        continue;
    }
    await appendFile(join(root, "requests.jsonl"), `${line}\n`);
    if (method.startsWith("thread/") || method.startsWith("turn/")) {
        await appendFile(join(root, "prompt-requests.jsonl"), `${JSON.stringify({ method, params })}\n`);
    }
    if (method === "initialize") {
        if (scenario === "hang-initialize") continue;
        initialized = true;
        result(id, { userAgent: "fake-codex/0.154.0", codexHome: root, platformFamily: "unix", platformOs: "linux" });
    }
    else if (method === "initialized") {
        acknowledged = initialized;
    }
    else if (!acknowledged) {
        send({ id, error: { code: -32000, message: "Initialization handshake missing" } });
    }
    else if (method === "account/read") {
        if (scenario === "hang" || scenario === "stubborn") continue;
        if (scenario === "crash") process.exit(7);
        if (scenario === "malformed") { process.stdout.write("{broken\n"); continue; }
        if (scenario === "oversized") { process.stdout.write("x".repeat(2 * 1024 * 1024 + 1)); continue; }
        if (scenario === "missing-required") { result(id, {}); continue; }
        if (scenario === "unknown-notification") send({ method: "future/notification", params: { anything: true } });
        if (scenario === "server-request") send({ id: "approval-42", method: "item/commandExecution/requestApproval", params: {} });
        if (scenario === "reordered") {
            reordered.push(id);
            if (reordered.length === 2) {
                for (const requestId of reordered.reverse()) result(requestId, { account: { type: "chatgpt", email: String(requestId), planType: "plus" }, requiresOpenaiAuth: true });
                reordered = [];
            }
            continue;
        }
        try {
            await readFile(join(root, "complete-login"));
            await finishLogin();
        }
        catch { }
        let account = null;
        try {
            account = JSON.parse(await readFile(accountFile, "utf8"));
        }
        catch { }
        if (account?.type === "apiKey")
            account = { type: "apiKey" };
        if (scenario === "fragmented") account = { type: "chatgpt", email: "测试🙂@example.test", planType: "plus" };
        result(id, { account, requiresOpenaiAuth: true });
    }
    else if (method === "account/login/start" && params.type === "chatgptDeviceCode") {
        if (scenario === "fail-device") {
            send({ id, error: { code: 400, message: "Upstream secret sk-THIS-MUST-NOT-LEAK" } });
            continue;
        }
        login = "test-login-id";
        result(id, { type: "chatgptDeviceCode", loginId: login,
            verificationUrl: scenario === "invalid-url" ? "https://attacker.example/codex/device" : "https://auth.openai.com/codex/device",
            userCode: "ABCD-1234" });
        if (scenario === "auto")
            timer = setTimeout(() => finishLogin(), 40);
        if (scenario === "expired")
            timer = setTimeout(() => {
                send({ method: "account/login/completed", params: { loginId: login, success: false, error: "secret-upstream-code" } });
                login = null;
            }, 40);
    }
    else if (method === "account/login/start" && params.type === "apiKey") {
        await writeFile(accountFile, JSON.stringify({ type: "apiKey", apiKey: params.apiKey }), { mode: 0o600 });
        // Real notifications can arrive before the request response.
        send({ method: "account/login/completed", params: { loginId: null, success: true } });
        send({ method: "account/updated", params: { authMode: "apikey" } });
        result(id, { type: "apiKey" });
    }
    else if (method === "account/login/cancel") {
        clearTimeout(timer);
        const loginId = login;
        login = null;
        result(id, { status: "canceled" });
        send({ method: "account/login/completed", params: { loginId, success: false } });
    }
    else if (method === "account/logout") {
        await unlink(accountFile).catch(() => { });
        result(id, {});
        send({ method: "account/updated", params: { authMode: null } });
    }
    else if (method === "thread/start") {
        result(id, { thread: { id: `test-thread-${++threadNumber}`, ephemeral: params.ephemeral,
                cliVersion: "0.154.0", createdAt: 1, updatedAt: 1, cwd: process.cwd(),
                modelProvider: "openai", preview: "", projectId: null, sessionId: "fixture", source: "exec",
                status: { type: "idle" }, turns: [] },
            model: "test-model", modelProvider: "openai", sandbox: { type: "readOnly", networkAccess: false },
            approvalPolicy: params.approvalPolicy, approvalsReviewer: "user", cwd: process.cwd() });
    }
    else if (method === "turn/start") {
        const threadId = params.threadId;
        const prompt = params.input[0].text;
        const turnId = `test-turn-${threadNumber}`;
        const started = { id: turnId, status: "inProgress", items: [] };
        send({ method: "turn/started", params: { threadId, turn: started } });
        function complete() {
            const failed = scenario === "prompt-fail" || scenario === "prompt-partial-failure" || prompt === "Trigger a simulated failure.";
            const message = { type: "agentMessage", id: "answer", phase: "final_answer",
                text: scenario === "prompt-large" ? "x".repeat(8001) : "Hello from the connected account." };
            send({ method: "item/completed", params: { completedAtMs: 1, threadId: "another-thread", turnId, item: { ...message, text: "Wrong thread" } } });
            send({ method: "item/completed", params: { completedAtMs: 1, threadId, turnId, item: { ...message, id: "commentary", phase: "commentary", text: "Thinking…" } } });
            if (scenario !== "prompt-empty" && scenario !== "prompt-fail") {
                send({ method: "item/completed", params: { completedAtMs: 1, threadId, turnId, item: message } });
            }
            const error = failed ? { message: "Upstream secret THIS-MUST-NOT-LEAK", codexErrorInfo: "unauthorized", additionalDetails: "PRIVATE-DETAILS" } : null;
            if (error)
                send({ method: "error", params: { threadId, turnId, willRetry: false, error } });
            send({ method: "turn/completed", params: { threadId, turn: { id: turnId,
                        status: failed ? "failed" : "completed", error, items: scenario === "prompt-empty" ? [] : [message] } } });
        }
        if (scenario === "prompt-delayed")
            promptTimer = setTimeout(complete, 150);
        else if (scenario !== "prompt-timeout")
            complete();
        // Deliberately deliver immediate events before the RPC response.
        result(id, { turn: started });
    }
    else if (method === "turn/interrupt") {
        clearTimeout(promptTimer);
        result(id, {});
        send({ method: "turn/completed", params: { threadId: params.threadId,
                turn: { id: params.turnId, status: "interrupted", items: [], error: null } } });
    }
    else if (method === "thread/unsubscribe") {
        result(id, { status: "unsubscribed" });
    }
    else {
        send({ id, error: { code: -32601, message: "Unsupported method" } });
    }
}
