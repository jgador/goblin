import type { WorkspaceContext } from "./navigation.js";
import type { RepositoryDrafts } from "./repository-setup.js";

type RenderOptions = {
    context: WorkspaceContext;
    approvalKey: string;
    authenticated: boolean;
    preserveHome: boolean;
    forceSelect: boolean;
    home: boolean;
    draft: string;
    sending: boolean;
    pending: boolean;
    workUnavailable: boolean;
    title: string;
    markup: () => string;
    settings?: { detach: () => void; attach: () => void };
};

export class WorkspaceDom {
    private renderedContext: WorkspaceContext | undefined;
    private dragging = false;

    constructor(
        private readonly root: HTMLElement,
        private readonly repositoryDrafts: RepositoryDrafts,
    ) {
        root.addEventListener("pointerdown", (event) => {
            if (
                event.target instanceof HTMLInputElement &&
                event.target.id === "work-effort"
            )
                this.dragging = true;
        });
        const stopDragging = () => {
            this.dragging = false;
        };
        document.addEventListener("pointerup", stopDragging);
        document.addEventListener("pointercancel", stopDragging);
    }

    get context() {
        return this.renderedContext;
    }
    get sliderDragging() {
        return this.dragging;
    }
    reset() {
        this.dragging = false;
    }

    private refreshHome(html: string, options: RenderOptions) {
        const next = document.createElement("template");
        next.innerHTML = html;
        // Keep the home, heading, composer and header mounted. Only server-backed
        // regions that actually changed need replacing during a refresh.
        for (const selector of [
            ".sidebar",
            ".workspace-notices",
            ".connection-choice",
            ".connection-nudge",
        ]) {
            const existing = this.root.querySelector(selector)!;
            const updated = next.content.querySelector(selector)!;
            if (!existing.isEqualNode(updated)) existing.replaceWith(updated);
        }
        const reply = this.root.querySelector<HTMLTextAreaElement>("#reply")!;
        if (reply.value !== options.draft) reply.value = options.draft;
        reply.disabled = options.sending;
        this.root.querySelector<HTMLButtonElement>(".send")!.disabled =
            options.sending || options.pending || options.workUnavailable;
    }

    render(options: RenderOptions) {
        const { root } = this;
        const {
            context,
            approvalKey,
            authenticated,
            preserveHome,
            forceSelect,
            home,
            settings,
        } = options;
        const active =
            document.activeElement instanceof HTMLElement &&
            root.contains(document.activeElement)
                ? document.activeElement
                : null;
        const activeButton = active?.closest<HTMLButtonElement>(
            "button[data-action]",
        );
        const buttonData = activeButton ? { ...activeButton.dataset } : null;
        const sameContext = context === this.renderedContext;
        const previousApproval =
            root.querySelector<HTMLElement>("#github-repository-options")
                ?.dataset.approval ?? "";
        const gitHubAuthorizationChanged =
            sameContext && approvalKey !== previousApproval;
        // Keep the native picker and its keyboard interaction alive during polling.
        // Loaded state is applied on the next render after the user leaves the select.
        if (
            (active?.closest("select") || this.sliderDragging) &&
            sameContext &&
            authenticated &&
            !options.sending &&
            !forceSelect &&
            !gitHubAuthorizationChanged
        )
            return false;
        if (
            authenticated &&
            this.renderedContext?.startsWith("work:") &&
            root.querySelector(".work-setup")
        )
            this.repositoryDrafts.remember(this.renderedContext, {
                approval: previousApproval,
                values: Array.from(
                    root.querySelectorAll<HTMLInputElement | HTMLSelectElement>(
                        ".work-setup input:not([readonly]),.work-setup select",
                    ),
                ).map((x) => [x.id, x.value]),
                open:
                    root.querySelector<HTMLDetailsElement>(
                        "#github-repository-options",
                    )?.open ?? false,
                advancedOpen:
                    root.querySelector<HTMLDetailsElement>(
                        "#github-repository-advanced",
                    )?.open ?? false,
            });
        const preserved = sameContext
            ? Array.from(
                  root.querySelectorAll<HTMLInputElement | HTMLSelectElement>(
                      "input[id],select[id]",
                  ),
              )
                  .filter(
                      (x) =>
                          !(
                              gitHubAuthorizationChanged &&
                              x.closest('[data-form="github-repository"]')
                          ) &&
                          ![
                              "work-model",
                              "work-effort",
                              "model-connection",
                              "work-view",
                          ].includes(x.id),
                  )
                  .map((x) => [x.id, x.value] as const)
            : [];
        const detailStates = sameContext
            ? Array.from(
                  root.querySelectorAll<HTMLDetailsElement>("details[id]"),
              )
                  .filter(
                      (x) =>
                          (x.open || x.classList.contains("latest-result")) &&
                          !(
                              gitHubAuthorizationChanged &&
                              (x.id === "github-repository-options" ||
                                  x.id === "github-repository-advanced")
                          ),
                  )
                  .map((x) => [x.id, x.open] as const)
            : [];
        const focus =
            active instanceof HTMLTextAreaElement ||
            active instanceof HTMLInputElement
                ? active
                : null;
        const activeId = active?.id;
        const activeSummary = active?.closest("summary")?.parentElement?.id;
        const cursor = focus?.selectionStart,
            cursorEnd = focus?.selectionEnd;
        const scrolls = Array.from(
            root.querySelectorAll<HTMLElement>("[data-scroll]"),
        ).map((x) => [x.dataset.scroll, x.scrollTop] as const);
        this.renderedContext = context;

        document.title = options.title;
        const html = options.markup();
        if (
            authenticated &&
            preserveHome &&
            sameContext &&
            home &&
            root.querySelector(".home")
        )
            this.refreshHome(html, options);
        else {
            // Keep live settings forms mounted across polling renders.
            settings?.detach();
            root.innerHTML = html;
            settings?.attach();
        }
        for (const [id, value] of preserved) {
            const field = document.getElementById(id) as
                HTMLInputElement | HTMLSelectElement | null;
            if (field) field.value = value;
        }
        for (const [id, open] of detailStates) {
            const detail = document.getElementById(
                id,
            ) as HTMLDetailsElement | null;
            if (detail) detail.open = open;
        }
        const setup = this.repositoryDrafts.restore(context, approvalKey);
        for (const [id, value] of setup?.values ?? []) {
            const field = document.getElementById(id) as
                HTMLInputElement | HTMLSelectElement | null;
            if (field) field.value = value;
        }
        const gitHubRepositoryOptions = root.querySelector<HTMLDetailsElement>(
            "#github-repository-options",
        );
        if (gitHubRepositoryOptions && setup?.open)
            gitHubRepositoryOptions.open = true;
        const advancedRepositoryOptions =
            root.querySelector<HTMLDetailsElement>(
                "#github-repository-advanced",
            );
        if (advancedRepositoryOptions && setup?.advancedOpen)
            advancedRepositoryOptions.open = true;
        this.repositoryDrafts.showErrors(root, context);
        if (focus?.id && sameContext) {
            const replacement = document.getElementById(focus.id) as
                HTMLInputElement | HTMLTextAreaElement | null;
            if (replacement && !replacement.closest("[inert]")) {
                replacement.focus({ preventScroll: true });
                if (
                    cursor != null &&
                    (replacement instanceof HTMLTextAreaElement ||
                        ["text", "search", "password"].includes(
                            replacement.type,
                        ))
                )
                    replacement.setSelectionRange(cursor, cursorEnd ?? cursor);
            }
        }
        if (buttonData?.action)
            Array.from(
                root.querySelectorAll<HTMLButtonElement>("button[data-action]"),
            )
                .find(
                    (b) =>
                        b.dataset.action === buttonData.action &&
                        b.dataset.id === buttonData.id &&
                        b.dataset.value === buttonData.value &&
                        b.dataset.provider === buttonData.provider &&
                        !b.closest("[inert]") &&
                        b.getClientRects().length,
                )
                ?.focus({ preventScroll: true });
        if (sameContext && !focus && !buttonData) {
            const replacement = activeSummary
                ? document
                      .getElementById(activeSummary)
                      ?.querySelector<HTMLElement>("summary")
                : activeId
                  ? document.getElementById(activeId)
                  : null;
            if (replacement && !replacement.closest("[inert]"))
                replacement.focus({ preventScroll: true });
        }
        for (const [name, top] of scrolls) {
            const element = root.querySelector<HTMLElement>(
                `[data-scroll="${name}"]`,
            );
            if (element && (sameContext || name === "sidebar"))
                element.scrollTop = top;
        }
        if (active?.isConnected && active.closest(".settings-page"))
            active.focus({ preventScroll: true });

        return true;
    }
}
