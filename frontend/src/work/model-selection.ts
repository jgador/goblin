import type { Work } from "./contracts.js";

export type ModelOption = {
    id: string;
    model: string;
    displayName: string;
    defaultReasoningEffort: string;
    supportedReasoningEfforts: string[];
    isDefault: boolean;
    isNew: boolean;
};
export type ModelCatalog = {
    models: ModelOption[];
    hasMore: boolean;
    defaultModel?: string;
    fetchedAt?: string;
    stale: boolean;
    refreshing: boolean;
    unavailable: boolean;
};
export type ModelQuery = { limit: 3 | 10; selectedModel: string };
export type ModelSelection = {
    connectionId: string;
    model: string;
    effort: string;
    touched: boolean;
    expanded: boolean;
    notice: string;
};
type ModelContext = {
    work?: Work;
    agent?: { connectionId: string; model?: string };
};
type SelectionStorage = Pick<Storage, "getItem" | "setItem" | "removeItem">;
const storageKey = "goblin.modelSelections";

export const effortStops = [
    { value: "low", label: "Low" },
    { value: "medium", label: "Medium" },
    { value: "high", label: "High" },
    { value: "xhigh", label: "Extra High" },
] as const;
export const effortLabel = (value: string) =>
    effortStops.find((stop) => stop.value === value)?.label ??
    value.slice(0, 1).toUpperCase() + value.slice(1);

export function selectedModel(
    catalog: ModelCatalog | undefined,
    model: string,
) {
    return catalog?.models.find((item) =>
        model ? item.model === model : item.isDefault,
    );
}

export function availableEfforts(
    catalog: ModelCatalog | undefined,
    model: string,
) {
    const selected = selectedModel(catalog, model);
    return effortStops
        .map((stop, index) => ({ ...stop, index }))
        .filter((stop) =>
            selected?.supportedReasoningEfforts.includes(stop.value),
        );
}

export class ModelSelections {
    private readonly storage: SelectionStorage;
    private readonly choices = new Map<string, ModelSelection>();

    constructor(storage: SelectionStorage) {
        this.storage = storage;
        try {
            const saved: unknown = JSON.parse(
                storage.getItem(storageKey) ?? "[]",
            );
            if (Array.isArray(saved))
                for (const entry of saved) {
                    if (!Array.isArray(entry) || typeof entry[0] !== "string")
                        continue;
                    const value: unknown = entry[1];
                    if (
                        !value ||
                        typeof value !== "object" ||
                        !("connectionId" in value) ||
                        typeof value.connectionId !== "string" ||
                        !("model" in value) ||
                        typeof value.model !== "string" ||
                        !("effort" in value) ||
                        typeof value.effort !== "string"
                    )
                        continue;
                    this.choices.set(entry[0], {
                        connectionId: value.connectionId,
                        model: value.model,
                        effort: value.effort,
                        touched: "touched" in value && !!value.touched,
                        expanded: "expanded" in value && !!value.expanded,
                        notice: "",
                    });
                }
        } catch {
            storage.removeItem(storageKey);
        }
    }

    get(context: ModelContext): Readonly<ModelSelection> {
        return this.choice(context);
    }

    selectModel(context: ModelContext, model: string) {
        const choice = this.choice(context);
        choice.model = model;
        choice.effort = "";
        choice.touched = true;
        choice.notice = "";
        this.save();
    }

    setEffort(context: ModelContext, effort: string, catalog?: ModelCatalog) {
        const choice = this.choice(context);
        if (
            !selectedModel(
                catalog,
                choice.model,
            )?.supportedReasoningEfforts.includes(effort)
        )
            return false;
        choice.effort = effort;
        choice.touched = true;
        this.save();
        return true;
    }

    expand(context: ModelContext) {
        this.choice(context).expanded = true;
        this.save();
    }

    reconcile(
        context: ModelContext,
        catalog: ModelCatalog,
        requestedModel: string,
    ) {
        const choice = this.choice(context);
        // A response for a previous choice must not replace a newer selection.
        if (choice.model !== requestedModel) return;
        if (
            choice.model &&
            !catalog.refreshing &&
            !catalog.unavailable &&
            catalog.models.length > 0 &&
            !selectedModel(catalog, choice.model)
        ) {
            choice.model = "";
            choice.effort = "";
            choice.touched = true;
            choice.notice =
                "The previous model is no longer listed. Codex default is selected.";
        }
        if (
            choice.effort &&
            !selectedModel(
                catalog,
                choice.model,
            )?.supportedReasoningEfforts.includes(choice.effort)
        )
            choice.effort = "";
        this.save();
    }

    clear() {
        this.choices.clear();
        this.storage.removeItem(storageKey);
    }

    private choice({ work, agent }: ModelContext): ModelSelection {
        const key = work?.id ?? "draft";
        let choice = this.choices.get(key);
        if (!choice) {
            const previous =
                work?.repositoryRequest?.target ??
                work?.attempts.at(-1)?.target;
            choice = {
                connectionId: agent?.connectionId ?? "",
                model: previous?.requestedModel ?? agent?.model ?? "",
                effort: previous?.requestedEffort ?? "",
                touched: !!previous,
                expanded: false,
                notice: "",
            };
            this.choices.set(key, choice);
        }
        if (agent && choice.connectionId !== agent.connectionId) {
            const selectedAnotherModel = !!(choice.model || choice.effort);
            Object.assign(choice, {
                connectionId: agent.connectionId,
                model: agent.model ?? "",
                effort: "",
                touched: false,
                expanded: false,
                notice: selectedAnotherModel
                    ? "The coding agent connection changed. Choose a model again."
                    : "",
            });
            this.save();
        } else if (
            agent &&
            !choice.touched &&
            !work?.attempts.length &&
            choice.model !== (agent.model ?? "")
        ) {
            choice.model = agent.model ?? "";
            choice.effort = "";
            this.save();
        }
        return choice;
    }

    private save() {
        this.storage.setItem(storageKey, JSON.stringify([...this.choices]));
    }
}
