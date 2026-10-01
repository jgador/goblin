import assert from "node:assert/strict";
import { test } from "node:test";
import { WorkAction } from "../../frontend/src/api/values.js";
import { CommandSubmission } from "../../frontend/src/work/command-submission.js";
import type { PendingCommand } from "../../frontend/src/work/command-submission.js";

const key = "goblin.pendingCommand";
const command: PendingCommand = {
    path: "/api/work/commands",
    body: {
        commandId: "9007199254740993",
        workId: "42",
        expectedVersion: "7",
        action: WorkAction.AddContext,
        text: "Preserve my context",
    },
};
function storage(saved?: string) {
    const items = new Map(saved === undefined ? [] : [[key, saved]]);
    return {
        getItem: (key: string) => items.get(key) ?? null,
        setItem: (key: string, value: string) => {
            items.set(key, value);
        },
        removeItem: (key: string) => {
            items.delete(key);
        },
    };
}

test("unconfirmed commands survive reload and resend the exact identity and payload only on request", async () => {
    const saved = storage();
    const dispatched: PendingCommand[] = [];
    const failed = new CommandSubmission(
        saved,
        async (pending) => {
            assert.deepEqual(JSON.parse(saved.getItem(key)!), pending);
            dispatched.push(pending);
            throw new Error("Save unconfirmed");
        },
        () => {},
    );
    await assert.rejects(failed.submit(command), {
        message: "Save unconfirmed",
    });
    assert.equal(failed.sending, false);
    assert.deepEqual(failed.pending, command);
    const recovered = new CommandSubmission(
        saved,
        async (pending) => {
            dispatched.push(pending);
        },
        () => {},
    );
    assert.equal(dispatched.length, 1);
    assert.deepEqual(recovered.pending, command);
    assert.deepEqual(await recovered.resend(), command);
    assert.deepEqual(dispatched, [command, command]);
    assert.equal(recovered.pending, null);
    assert.equal(saved.getItem(key), null);
});

test("identity preparation blocks overlapping submissions and dismissal", async () => {
    const saved = storage();
    let release!: () => void;
    const waiting = new Promise<void>((resolve) => {
        release = resolve;
    });
    let sends = 0;
    const submission = new CommandSubmission(
        saved,
        async () => {
            sends++;
        },
        () => {},
    );
    const first = submission.submit(async () => {
        await waiting;
        return command;
    });
    assert.equal(submission.sending, true);
    assert.equal(await submission.submit(command), undefined);
    submission.dismiss();
    release();
    await first;
    assert.equal(sends, 1);
    assert.equal(submission.pending, null);
});

test("a pending dispatch blocks replacement, resend, and dismissal until it settles", async () => {
    const saved = storage();
    let release!: () => void;
    let started!: () => void;
    const waiting = new Promise<void>((resolve) => {
        release = resolve;
    });
    const dispatched = new Promise<void>((resolve) => {
        started = resolve;
    });
    let sends = 0;
    const submission = new CommandSubmission(
        saved,
        async () => {
            sends++;
            started();
            await waiting;
        },
        () => {},
    );
    const first = submission.submit(command);
    await dispatched;
    assert.equal(await submission.submit(command), undefined);
    assert.equal(await submission.resend(), undefined);
    submission.dismiss();
    assert.deepEqual(submission.pending, command);
    assert.deepEqual(JSON.parse(saved.getItem(key)!), command);
    release();
    await first;
    assert.equal(sends, 1);
    assert.equal(submission.pending, null);
});

test("conversation recovery preserves the original message and linked Work identity", async () => {
    const conversation: PendingCommand = {
        path: "/api/conversations/commands",
        body: {
            conversationId: "42",
            messageId: "9007199254740993",
            text: null,
            workId: "9007199254740994",
        },
    };
    const saved = storage(JSON.stringify(conversation));
    const submission = new CommandSubmission(
        saved,
        async (pending) => {
            assert.deepEqual(pending, conversation);
        },
        () => {},
    );
    assert.deepEqual(await submission.resend(), conversation);
    assert.equal(saved.getItem(key), null);
});

test("preparation and storage failures never dispatch an untracked command", async () => {
    let sends = 0;
    const saved = storage();
    const submission = new CommandSubmission(
        saved,
        async () => {
            sends++;
        },
        () => {},
    );
    await assert.rejects(
        submission.submit(async () => {
            throw new Error("Identity reservation failed");
        }),
    );
    saved.setItem = () => {
        throw new Error("Storage full");
    };
    await assert.rejects(submission.submit(command), {
        message: "Storage full",
    });
    assert.equal(sends, 0);
    assert.equal(submission.sending, false);
    assert.equal(submission.pending, null);
});

test("dismissal retains server state and sends no replacement command", () => {
    const saved = storage(JSON.stringify(command));
    const submission = new CommandSubmission(
        saved,
        async () => {
            assert.fail("Dismissal must not dispatch");
        },
        () => {},
    );
    submission.dismiss();
    assert.equal(submission.pending, null);
    assert.equal(saved.getItem(key), null);
});

for (const value of [
    "{",
    "null",
    JSON.stringify({ ...command, path: "/api/session/lock" }),
    JSON.stringify({
        ...command,
        body: { ...command.body, action: "execute" },
    }),
    JSON.stringify({
        ...command,
        body: { ...command.body, commandId: 9007199254740992 },
    }),
]) {
    test(`invalid saved command is reported without crashing startup: ${value}`, () => {
        const saved = storage(value);
        const submission = new CommandSubmission(
            saved,
            async () => {
                assert.fail("Restoration must not dispatch");
            },
            () => {},
        );
        assert.equal(submission.pending, null);
        assert.match(submission.recoveryNotice, /could not be recovered/);
        assert.equal(saved.getItem(key), null);
    });
}
