import {
    settingsHref,
    settingsSelection,
    settingsTarget,
} from "../settings/navigation.js";
import type { SettingsSelection } from "../settings/navigation.js";
import type { View } from "./contracts.js";

export type WorkspaceView = "work" | "chat" | "new" | "knowledge" | "settings";
export type WorkspaceContext =
    | "new"
    | "knowledge"
    | `work:${string}`
    | `chat:${string}`
    | `settings:${SettingsSelection}`;
type Destination = { view: WorkspaceView; id: string };

function directory(path: string) {
    if (/^\/knowledge\/?$/.test(path)) return "knowledge";
    if (/^\/settings\/?$/.test(path)) return "settings";
}

export class WorkspaceNavigation {
    readonly query = new URLSearchParams(location.search);
    private workId =
        this.query.get("item") ??
        sessionStorage.getItem("goblin.selectedWork") ??
        "";
    private chatId = this.query.get("conversation") ?? "";
    private page: WorkspaceView =
        directory(location.pathname) ??
        (this.query.has("conversation")
            ? "chat"
            : location.pathname.startsWith("/work") && this.workId
              ? "work"
              : "new");
    private section = settingsSelection(this.query);
    private returnDestination: Destination = { view: "new", id: "" };
    private chooseInitialWork =
        !this.isDirectory &&
        !this.query.has("new") &&
        !this.query.has("conversation");

    constructor() {
        if (this.page === "settings")
            history.replaceState(null, "", settingsHref(this.section));
    }

    get view() {
        return this.page;
    }
    get selected() {
        return this.workId;
    }
    get activeChat() {
        return this.chatId;
    }
    get settingsSection() {
        return this.section;
    }
    get settingsReturn(): Readonly<Destination> {
        return this.returnDestination;
    }
    get isDirectory() {
        return this.page === "knowledge" || this.page === "settings";
    }

    get context(): WorkspaceContext {
        if (this.page === "work") return `work:${this.workId}`;
        if (this.page === "chat") return `chat:${this.chatId}`;
        if (this.page === "settings") return `settings:${this.section}`;
        return this.page;
    }

    initialWork(items: readonly View[]) {
        if (!this.chooseInitialWork) return;
        this.chooseInitialWork = false;
        return (
            items.find((item) => item.work.id === this.workId) ??
            items.toSorted(
                (a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt),
            )[0]
        )?.work.id;
    }

    showHomeIfSelectionMissing(items: readonly View[]) {
        if (
            this.page === "work" &&
            !items.some((item) => item.work.id === this.workId)
        )
            this.page = "new";
    }

    locationTarget(): Destination {
        const params = new URLSearchParams(location.search);
        const page = directory(location.pathname);
        if (page === "settings")
            return { view: page, id: settingsSelection(params) };
        if (page === "knowledge") return { view: page, id: "" };
        const item = params.get("item");
        const conversation = params.get("conversation");
        return {
            view: item ? "work" : conversation ? "chat" : "new",
            id: item ?? conversation ?? "",
        };
    }

    navigate(next: WorkspaceView, id: string, mode: "auto" | "restore") {
        this.chooseInitialWork = false;
        if (next === "settings" && this.page !== "settings")
            this.returnDestination = {
                view: this.page,
                id:
                    this.page === "work"
                        ? this.workId
                        : this.page === "chat"
                          ? this.chatId
                          : "",
            };
        if (
            mode === "auto" &&
            (next !== this.page ||
                (next === "settings" && id !== this.section)) &&
            (this.isDirectory || next === "knowledge" || next === "settings")
        )
            history.pushState(null, "", location.href);
        this.page = next;
        let path: string;
        if (next === "work") {
            this.workId = id;
            sessionStorage.setItem("goblin.selectedWork", id);
            path = `/work?item=${encodeURIComponent(id)}`;
        } else if (next === "chat") {
            this.chatId = id;
            path = `/?conversation=${encodeURIComponent(id)}`;
        } else if (next === "settings") {
            this.section = settingsTarget(id);
            path = settingsHref(this.section);
        } else path = next === "knowledge" ? "/knowledge" : "/?new=work";
        history.replaceState(null, "", path);
    }
}
