import { PublicError, runtimeError } from "./errors.js";
import { isRecord } from "./json.js";
import type { Codex } from "./codex.js";
import type { CodexNotification } from "./protocol.js";
import type { PromptResult } from "../shared/api.js";

const maxReplyLength = 8000;
const cancelled = () => new PublicError("prompt_cancelled", "The prompt test was cancelled.", 408);

function generationError(error: unknown) {
  const info = isRecord(error) ? error.codexErrorInfo : undefined;
  const status = isRecord(info)
    ? Object.values(info).filter(isRecord).find((value) => Number.isInteger(value.httpStatusCode))?.httpStatusCode
    : null;
  if (info === "unauthorized" || status === 401) {
    return new PublicError("prompt_unauthorized", "OpenAI rejected the saved login. Disconnect Codex and sign in again.", 502);
  }
  if (info === "usageLimitExceeded" || info === "rateLimitExceeded" || status === 429) {
    return new PublicError("prompt_limit_reached", "The connected account has reached a usage or rate limit. Check your plan or API billing, then retry later.", 429);
  }
  if (status === 403) {
    return new PublicError("prompt_access_denied", "The connected account cannot use this model. Check your account's model access and permissions.", 502);
  }
  return new PublicError("prompt_failed", "The model request failed. Check your account's model access and billing, then retry.", 502);
}

// One fresh, ephemeral thread per test. Only final assistant text is returned;
// raw events, reasoning, error bodies, and credentials never reach the browser.
export async function runPrompt(codex: Codex, prompt: string,
  { signal, timeoutMs = 90_000 }: { signal?: AbortSignal; timeoutMs?: number } = {},
): Promise<Omit<PromptResult, "authType">> {
  if (signal?.aborted) throw cancelled();
  const startedAt = Date.now();
  let threadId: string | undefined;
  let turnId: string | undefined;
  let finished = false;
  let timer: NodeJS.Timeout | undefined;
  const messages = new Map<string, string>();
  const { promise: completion, resolve: resolveTurn, reject: rejectTurn } = Promise.withResolvers<string>();
  // Notifications can finish the turn before turn/start's response arrives.
  completion.catch(() => {});

  function saveMessage(item: unknown) {
    if (!isRecord(item) || item.type !== "agentMessage" || item.phase === "commentary") return;
    if (typeof item.id !== "string" || typeof item.text !== "string") return;
    messages.set(item.id, item.text);
    if (messages.size > 32 || [...messages.values()].join("\n\n").length > maxReplyLength) {
      rejectTurn(new PublicError("prompt_reply_too_large", "The reply was too long for a connection test. Try a shorter prompt.", 502));
    }
  }

  function notification({ method, params }: CodexNotification) {
    if (!threadId || params?.threadId !== threadId) return;
    const turn = isRecord(params.turn) ? params.turn : undefined;
    const eventTurnId = params.turnId ?? turn?.id;
    if (typeof eventTurnId !== "string") return;
    if (turnId && eventTurnId !== turnId) return;
    turnId ??= eventTurnId;
    if (method === "item/completed") saveMessage(params.item);
    if (method === "turn/completed" && turn) {
      finished = true;
      if (turn.status === "failed") return rejectTurn(generationError(turn.error));
      if (turn.status !== "completed") return rejectTurn(cancelled());
      for (const item of Array.isArray(turn.items) ? turn.items : []) saveMessage(item);
      const reply = [...messages.values()].join("\n\n").trim();
      if (!reply) return rejectTurn(new PublicError("prompt_empty_reply", "The model finished without a text reply. Please retry.", 502));
      resolveTurn(reply);
    }
  }

  const disconnected = () => rejectTurn(runtimeError());
  const abort = () => rejectTurn(cancelled());
  codex.on("notification", notification);
  codex.on("disconnected", disconnected);
  signal?.addEventListener("abort", abort, { once: true });
  try {
    const thread = await codex.request("thread/start", {
      cwd: codex.options.workspace,
      ephemeral: true,
      approvalPolicy: "never",
      sandbox: "read-only",
      baseInstructions: "Answer the user's connection-test prompt briefly, using only the text in this conversation. Do not call tools, inspect files, browse, or perform any actions.",
    });
    threadId = thread?.thread?.id;
    if (typeof threadId !== "string" || thread.thread.ephemeral !== true ||
        thread.modelProvider !== "openai" || thread.sandbox?.type !== "readOnly" ||
        thread.approvalPolicy !== "never" || typeof thread.model !== "string") {
      throw new PublicError("prompt_configuration_error", "Codex could not create an isolated prompt test. Check the pinned runtime version.", 502);
    }
    if (signal?.aborted) throw cancelled();
    const started = await codex.request("turn/start", {
      threadId,
      input: [{ type: "text", text: prompt }],
      approvalPolicy: "never",
      sandboxPolicy: { type: "readOnly", networkAccess: false },
    });
    if (typeof started?.turn?.id !== "string" || (turnId && turnId !== started.turn.id)) throw runtimeError();
    turnId = started.turn.id;
    timer = setTimeout(() => rejectTurn(new PublicError("prompt_timeout", "The prompt test took too long and was cancelled. Please retry.", 504)), timeoutMs);
    timer.unref();
    const reply = await completion;
    return { reply, model: thread.model, durationMs: Date.now() - startedAt };
  } finally {
    clearTimeout(timer);
    signal?.removeEventListener("abort", abort);
    codex.off("notification", notification);
    codex.off("disconnected", disconnected);
    if (threadId && codex.ready) {
      if (turnId && !finished) {
        await codex.request("turn/interrupt", { threadId, turnId }).catch(() => {
          if (codex.child) codex.fail(codex.child);
        });
      }
      if (codex.ready) await codex.request("thread/unsubscribe", { threadId }).catch(() => {});
    }
  }
}
