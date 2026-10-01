import type { ModelCatalog, ModelQuery } from "./model-selection.js";

type ModelApi = <T>(path: string, body?: unknown) => Promise<T>;
type CatalogEntry = {
    catalog?: ModelCatalog;
    query?: string;
    requestedAt: number;
    error?: string;
    request?: Promise<void>;
    timer?: ReturnType<typeof setTimeout>;
};
const retryInterval = 60_000;
const catalogLifetime = 86_400_000;

export class ModelCatalogs {
    private readonly api: ModelApi;
    private readonly changed: (
        connectionId: string,
        selectedModel: string,
    ) => void;
    private readonly currentQuery: (
        connectionId: string,
    ) => ModelQuery | undefined;
    private readonly entries = new Map<string, CatalogEntry>();

    constructor(
        api: ModelApi,
        changed: (connectionId: string, selectedModel: string) => void,
        currentQuery: (connectionId: string) => ModelQuery | undefined,
    ) {
        this.api = api;
        this.changed = changed;
        this.currentQuery = currentQuery;
    }

    get(connectionId: string) {
        const entry = this.entries.get(connectionId);
        return {
            catalog: entry?.catalog,
            error: entry?.error,
            loading: !!entry?.request,
        };
    }

    load(
        connectionId: string,
        query: ModelQuery,
        force = false,
    ): Promise<void> {
        const entry = this.entry(connectionId);
        if (entry.request) return entry.request;
        const fetchedAt = Date.parse(entry.catalog?.fetchedAt ?? "");
        const retryAt = entry.requestedAt + retryInterval;
        const nextCheck =
            entry.error ||
            entry.catalog?.stale ||
            entry.catalog?.unavailable ||
            !Number.isFinite(fetchedAt)
                ? retryAt
                : Math.max(retryAt, fetchedAt + catalogLifetime);
        if (
            !force &&
            entry.query === this.queryKey(query) &&
            Date.now() < nextCheck
        )
            return Promise.resolve();
        return this.start(connectionId, entry, query, false);
    }

    refresh(connectionId: string, query: ModelQuery): Promise<void> {
        const entry = this.entry(connectionId);
        if (entry.request) return entry.request;
        return this.start(connectionId, entry, query, true);
    }

    invalidate(connectionId: string) {
        const entry = this.entries.get(connectionId);
        clearTimeout(entry?.timer);
        // Retain the last displayable catalog while detaching any obsolete request.
        this.entries.set(connectionId, {
            catalog: entry?.catalog,
            requestedAt: 0,
        });
    }

    clear() {
        for (const entry of this.entries.values()) clearTimeout(entry.timer);
        this.entries.clear();
    }

    private entry(connectionId: string) {
        let entry = this.entries.get(connectionId);
        if (!entry) {
            entry = { requestedAt: 0 };
            this.entries.set(connectionId, entry);
        }
        return entry;
    }

    private queryKey(query: ModelQuery) {
        return `${query.limit}:${query.selectedModel}`;
    }

    private start(
        connectionId: string,
        entry: CatalogEntry,
        query: ModelQuery,
        refresh: boolean,
    ) {
        clearTimeout(entry.timer);
        entry.requestedAt = Date.now();
        entry.request = Promise.resolve().then(() =>
            this.fetch(connectionId, entry, query, refresh),
        );
        return entry.request;
    }

    private async fetch(
        connectionId: string,
        entry: CatalogEntry,
        query: ModelQuery,
        refresh: boolean,
    ) {
        const current = () => this.entries.get(connectionId) === entry;
        if (!current()) return;
        try {
            const path = `/api/connections/${encodeURIComponent(connectionId)}/models`;
            if (refresh) {
                await this.api(`${path}/refresh`, {});
                if (!current()) return;
            }
            const params = new URLSearchParams({ limit: String(query.limit) });
            if (query.selectedModel)
                params.set("selected", query.selectedModel);
            const catalog = await this.api<ModelCatalog>(`${path}?${params}`);
            if (!current()) return;
            entry.catalog = catalog;
            entry.error = undefined;
        } catch (failure) {
            if (!current()) return;
            entry.error =
                failure instanceof Error
                    ? failure.message
                    : "The model list could not be loaded.";
        } finally {
            // Old completions cannot clear loading state or schedule timers for a new entry.
            if (current()) {
                entry.query = this.queryKey(query);
                entry.request = undefined;
                if (!entry.error && entry.catalog?.refreshing)
                    entry.timer = setTimeout(() => {
                        if (!current()) return;
                        const next = this.currentQuery(connectionId);
                        if (next) void this.load(connectionId, next, true);
                    }, 2000);
                this.changed(connectionId, query.selectedModel);
            }
        }
    }
}
