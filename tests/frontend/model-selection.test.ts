import assert from "node:assert/strict";
import { test } from "node:test";
import { WorkStatus } from "../../frontend/src/api/values.js";
import type { Work } from "../../frontend/src/work/contracts.js";
import {
    ModelSelections,
    availableEfforts,
    selectedModel,
} from "../../frontend/src/work/model-selection.js";
import type { ModelCatalog } from "../../frontend/src/work/model-selection.js";

function storage(saved?: string) {
    const items = new Map<string, string>();
    if (saved) items.set("goblin.modelSelections", saved);
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
function work(id = "1"): Work {
    return {
        id,
        objective: "Test",
        status: WorkStatus.Ready,
        attempts: [],
        history: [],
        decisions: [],
        results: [],
        artifacts: [],
    };
}
const agent = { connectionId: "1", model: "default" };
const catalog: ModelCatalog = {
    models: [
        {
            id: "1",
            model: "default",
            displayName: "Default",
            defaultReasoningEffort: "medium",
            supportedReasoningEfforts: ["low", "high"],
            isDefault: true,
            isNew: false,
        },
        {
            id: "2",
            model: "other",
            displayName: "Other",
            defaultReasoningEffort: "low",
            supportedReasoningEfforts: ["low"],
            isDefault: false,
            isNew: false,
        },
    ],
    hasMore: false,
    stale: false,
    refreshing: false,
    unavailable: false,
};

test("model, effort, and expanded choices survive reload and remain separate for each Work", () => {
    const saved = storage();
    const selections = new ModelSelections(saved);
    const first = { work: work(), agent };
    const second = { work: work("2"), agent };
    selections.selectModel(first, "other");
    selections.setEffort(first, "low", catalog);
    selections.expand(first);
    const reloaded = new ModelSelections(saved);
    assert.deepEqual(reloaded.get(first), {
        connectionId: "1",
        model: "other",
        effort: "low",
        touched: true,
        expanded: true,
        notice: "",
    });
    assert.equal(reloaded.get(second).model, "default");
    reloaded.clear();
    assert.equal(saved.getItem("goblin.modelSelections"), null);
    assert.equal(reloaded.get(first).model, "default");
});

test("malformed storage cannot crash selection loading or inject unexpected fields", () => {
    const broken = storage("{broken");
    assert.equal(new ModelSelections(broken).get({ agent }).model, "default");
    assert.equal(broken.getItem("goblin.modelSelections"), null);
    const mixed = storage(
        JSON.stringify([
            ["ignored", null],
            ["ignored", 1],
            ["ignored", { model: 4 }],
            [
                "1",
                {
                    connectionId: "1",
                    model: "other",
                    effort: "low",
                    touched: true,
                    expanded: true,
                    notice: "Stale warning",
                    unexpected: "not a selection field",
                },
            ],
        ]),
    );
    assert.deepEqual(new ModelSelections(mixed).get({ work: work(), agent }), {
        connectionId: "1",
        model: "other",
        effort: "low",
        touched: true,
        expanded: true,
        notice: "",
    });
});

test("changing the assigned connection resets choices and persists the new connection", () => {
    const saved = storage();
    const selections = new ModelSelections(saved);
    const context = { work: work(), agent };
    selections.selectModel(context, "other");
    selections.setEffort(context, "low", catalog);
    selections.expand(context);
    const next = { ...context, agent: { connectionId: "2", model: "next" } };
    const choice = selections.get(next);
    assert.equal(choice.model, "next");
    assert.equal(choice.connectionId, "2");
    assert.equal(choice.effort, "");
    assert.equal(choice.expanded, false);
    assert.equal(choice.touched, false);
    assert.match(choice.notice, /connection changed/);
    assert.equal(new ModelSelections(saved).get(next).connectionId, "2");
});

test("agent defaults update untouched work without replacing an explicit choice", () => {
    const selections = new ModelSelections(storage());
    const context = { work: work(), agent };
    assert.equal(selections.get(context).model, "default");
    assert.equal(
        selections.get({ ...context, agent: { ...agent, model: "next" } })
            .model,
        "next",
    );
    selections.selectModel(context, "other");
    assert.equal(
        selections.get({ ...context, agent: { ...agent, model: "next" } })
            .model,
        "other",
    );
});

test("repository requests seed the next selection ahead of prior attempt choices", () => {
    const selections = new ModelSelections(storage());
    const item = work();
    item.attempts.push({
        id: "1",
        status: "Succeeded",
        target: {
            runtime: "codex",
            requestedModel: "past",
            requestedEffort: "high",
        },
    });
    const context = { work: item, agent };
    assert.equal(selections.get(context).model, "past");
    assert.equal(selections.get(context).effort, "high");
    selections.clear();
    item.repositoryRequest = {
        repositories: ["owner/repo"],
        target: {
            runtime: "codex",
            requestedModel: "requested",
            requestedEffort: "low",
        },
    };
    assert.equal(selections.get(context).model, "requested");
    assert.equal(selections.get(context).effort, "low");
});

test("changing models resets effort and unsupported effort choices are rejected", () => {
    const selections = new ModelSelections(storage());
    const context = { work: work(), agent };
    assert.equal(selections.setEffort(context, "high", catalog), true);
    selections.selectModel(context, "other");
    assert.equal(selections.get(context).effort, "");
    assert.equal(selections.setEffort(context, "high", catalog), false);
    assert.equal(selections.get(context).effort, "");
    assert.equal(selections.setEffort(context, "low", catalog), true);
    assert.deepEqual(
        availableEfforts(catalog, "default").map((stop) => stop.index),
        [0, 2],
    );
    assert.equal(selectedModel(catalog, "")?.model, "default");
    assert.equal(selectedModel(catalog, "missing"), undefined);
});

test("an older catalog response cannot replace a newer model or effort choice", () => {
    const selections = new ModelSelections(storage());
    const context = { work: work(), agent };
    selections.selectModel(context, "other");
    selections.setEffort(context, "low", catalog);
    selections.reconcile(
        context,
        { ...catalog, models: [catalog.models[0]] },
        "default",
    );
    assert.equal(selections.get(context).model, "other");
    assert.equal(selections.get(context).effort, "low");
});

test("refreshing, unavailable, and empty catalogs do not discard the requested model", () => {
    const selections = new ModelSelections(storage());
    const context = { work: work(), agent };
    selections.selectModel(context, "missing");
    for (const partial of [
        { refreshing: true },
        { unavailable: true },
        { models: [] },
    ]) {
        selections.reconcile(context, { ...catalog, ...partial }, "missing");
        assert.equal(selections.get(context).model, "missing");
    }
    selections.reconcile(context, catalog, "missing");
    assert.equal(selections.get(context).model, "");
    assert.match(selections.get(context).notice, /no longer listed/);
});

test("reconciliation removes effort that the selected model no longer supports", () => {
    const selections = new ModelSelections(storage());
    const context = { work: work(), agent };
    selections.setEffort(context, "high", catalog);
    selections.reconcile(
        context,
        {
            ...catalog,
            models: [
                { ...catalog.models[0], supportedReasoningEfforts: ["low"] },
            ],
        },
        "default",
    );
    assert.equal(selections.get(context).model, "default");
    assert.equal(selections.get(context).effort, "");
});
