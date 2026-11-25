import {
    settingsHref,
    settingsSelection,
    settingsTarget,
} from "../settings/navigation.js";
import type { SettingsSelection } from "../settings/navigation.js";

export type WorkspaceView =
    "board" | "work" | "chat" | "new" | "knowledge" | "settings";
export type WorkspaceContext =
    | "board"
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
    private workId = "";
    private chatId = "";
    private page: WorkspaceView;
    private section = settingsSelection(this.query);
    private returnDestination: Destination = { view: "board", id: "" };

    constructor() {
        const { view, id } = this.locationTarget();
        this.page = view;
        if (view === "work") this.workId = id;
        if (view === "chat") this.chatId = id;
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
    get canReturnToBoard() {
        return history.state?.returnToBoard === true;
    }
    get inboxSelection() {
        return this.page === "work" && history.state?.inboxSelection === true;
    }

    expandWork() {
        history.replaceState({ ...history.state, inboxSelection: false }, "");
    }

    get context(): WorkspaceContext {
        if (this.page === "work") return `work:${this.workId}`;
        if (this.page === "chat") return `chat:${this.chatId}`;
        if (this.page === "settings") return `settings:${this.section}`;
        return this.page;
    }

    locationTarget(): Destination {
        const params = new URLSearchParams(location.search);
        const page = directory(location.pathname);
        if (page === "settings")
            return { view: page, id: settingsSelection(params) };
        if (page === "knowledge") return { view: page, id: "" };
        const item = params.get("item");
        const conversation = params.get("conversation");
        if (item) return { view: "work", id: item };
        if (conversation !== null) return { view: "chat", id: conversation };
        return { view: params.has("new") ? "new" : "board", id: "" };
    }

    navigate(next: WorkspaceView, id: string, mode: "auto" | "restore") {
        const previous = this.page;
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
        const push =
            mode === "auto" &&
            (next !== this.page ||
                (next === "settings" && id !== this.section)) &&
            (this.isDirectory ||
                next === "knowledge" ||
                next === "settings" ||
                (this.page === "board" && next !== "board"));
        this.page = next;
        let path: string;
        if (next === "work") {
            this.workId = id;
            path = `/work?item=${encodeURIComponent(id)}`;
        } else if (next === "chat") {
            this.chatId = id;
            path = `/?conversation=${encodeURIComponent(id)}`;
        } else if (next === "settings") {
            this.section = settingsTarget(id);
            path = settingsHref(this.section);
        } else if (next === "knowledge") path = "/knowledge";
        else path = next === "board" ? "/work" : "/?new=work";
        if (push)
            history.pushState(
                {
                    returnToBoard: previous === "board",
                    inboxSelection: previous === "board" && next === "work",
                },
                "",
                path,
            );
        else {
            const locationView = this.locationTarget();
            if (
                mode !== "restore" ||
                locationView.view !== next ||
                locationView.id !== id
            )
                history.replaceState(
                    next === "board" ? null : history.state,
                    "",
                    path,
                );
        }
    }
}
