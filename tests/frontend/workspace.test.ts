import assert from "node:assert/strict";
import { test } from "node:test";
import { WorkspaceController } from "../../frontend/src/work/workspace.js";

test("connection observations stay available when the Work read fails", async (t) => {
    const connection = {
        id: "2",
        runtime: "codex",
        name: "Codex",
        availability: "Available",
    };
    t.mock.method(globalThis, "fetch", async (path: string) => {
        if (path === "/api/work") return Response.json({}, { status: 503 });
        if (path === "/api/connections") return Response.json([connection]);
        return Response.json([]);
    });
    const workspace = new WorkspaceController(() =>
        assert.fail("unexpected lock"),
    );

    const [loaded, available] = await Promise.all([
        workspace.refreshWork(),
        workspace.refreshConnections(),
    ]);

    assert.equal(loaded, false);
    assert.equal(workspace.state.workUnavailable, true);
    assert.equal(workspace.state.connectionsLoaded, true);
    assert.deepEqual(workspace.state.connections, [connection]);
    assert.deepEqual(available, [connection.id]);
    assert.deepEqual(await workspace.refreshConnections(), []);
});

test("reset discards an old Work read without cancelling an in-flight command", async (t) => {
    const { promise: pending, resolve: release } =
        Promise.withResolvers<void>();
    const work = { version: "1", work: { id: "3" } };
    let currentSession = false;
    t.mock.method(
        globalThis,
        "fetch",
        async (path: string, request: RequestInit) => {
            if (path === "/api/work" && currentSession)
                return Response.json([work]);
            if (path === "/api/work") {
                await pending;
                return Response.json([{ version: "1", work: { id: "1" } }]);
            }
            if (path === "/api/work/commands") {
                assert.equal(request.signal, undefined);
                await pending;
                return Response.json({ saved: true });
            }
            return Response.json([]);
        },
    );
    const workspace = new WorkspaceController(() =>
        assert.fail("unexpected lock"),
    );

    const oldRead = workspace.refreshWork();
    const command = workspace.request("/api/work/commands", { commandId: "4" });
    workspace.reset();
    currentSession = true;
    assert.equal(await workspace.refreshWork(), true);
    release();

    await assert.rejects(oldRead, { name: "AbortError" });
    assert.deepEqual(workspace.state.work, [work]);
    assert.deepEqual(await command, { saved: true });
});
