const storageKey = "goblin.changesWidth";
const defaultWidth = 0.48;
const minimumPaneWidth = 320;
const keyboardStep = 24;

function savedWidth() {
    try {
        const value = Number(localStorage.getItem(storageKey));
        if (value > 0 && value < 1) return value;
    } catch {
        // The layout remains usable when browser storage is unavailable.
    }
    return defaultWidth;
}

type ResizeDrag = {
    pointerId: number;
    startX: number;
    startWidth: number;
    width: number;
};

export class ChangesLayout {
    private layout?: HTMLElement;
    private separator?: HTMLElement;
    private availableWidth = 0;
    private preferredWidth = savedWidth();
    private drag?: ResizeDrag;
    private frame?: number;
    private readonly observer = new ResizeObserver(() => this.measure());

    constructor(private readonly root: HTMLElement) {
        root.addEventListener("pointerdown", (event) => {
            if (
                event.target !== this.separator ||
                !this.availableWidth ||
                !event.isPrimary ||
                event.button !== 0 ||
                this.drag
            )
                return;
            const width = this.limit(this.preferredWidth);
            this.drag = {
                pointerId: event.pointerId,
                startX: event.clientX,
                startWidth: width,
                width,
            };
            event.preventDefault();
            this.separator.focus({ preventScroll: true });
            // The workspace root stays mounted when a refresh replaces the layout.
            root.setPointerCapture(event.pointerId);
            this.apply();
        });
        root.addEventListener("pointermove", (event) => {
            if (!this.updateDrag(event) || this.frame !== undefined) return;
            this.frame = requestAnimationFrame(() => {
                this.frame = undefined;
                this.apply();
            });
        });
        root.addEventListener("pointerup", (event) => {
            if (this.updateDrag(event)) this.endDrag(true);
        });
        const cancel = (event: PointerEvent) => {
            if (event.pointerId === this.drag?.pointerId) this.endDrag(false);
        };
        root.addEventListener("pointercancel", cancel);
        root.addEventListener("lostpointercapture", cancel);
        root.addEventListener("keydown", (event) => this.onKeydown(event));
        root.addEventListener("click", (event) => {
            if (
                event.target instanceof Element &&
                event.target.closest(".reset-changes-width")
            ) {
                this.endDrag(false);
                this.save(defaultWidth);
                this.apply();
            }
        });
    }

    attach(panel?: HTMLElement) {
        this.observer.disconnect();
        this.layout =
            panel?.closest<HTMLElement>(".work-review-layout") ?? undefined;
        this.separator =
            panel?.querySelector<HTMLElement>(".work-changes-resizer") ??
            undefined;
        if (this.layout) this.observer.observe(this.layout);
        this.measure();
    }

    private get min() {
        return Math.min(defaultWidth, minimumPaneWidth / this.availableWidth);
    }

    private get max() {
        return Math.max(
            defaultWidth,
            1 - minimumPaneWidth / this.availableWidth,
        );
    }

    private limit(width: number) {
        return Math.min(this.max, Math.max(this.min, width));
    }

    private measure() {
        const width =
            this.layout && this.separator?.offsetWidth
                ? this.layout.clientWidth
                : 0;
        if (width !== this.availableWidth) {
            this.availableWidth = width;
            this.endDrag(false);
        }
        this.apply();
    }

    private apply() {
        if (!this.layout || !this.separator || !this.availableWidth) return;
        const width = this.limit(this.drag?.width ?? this.preferredWidth);
        this.layout.toggleAttribute("data-resizing", !!this.drag);
        this.layout.style.setProperty(
            "--work-changes-width",
            `${width * 100}%`,
        );
        const percent = (share: number) => String(Math.round(share * 100));
        this.separator.setAttribute("aria-valuemin", percent(this.min));
        this.separator.setAttribute("aria-valuemax", percent(this.max));
        this.separator.setAttribute("aria-valuenow", percent(width));
        this.separator.setAttribute(
            "aria-valuetext",
            `${percent(width)}% for Changes`,
        );
    }

    private updateDrag(event: PointerEvent) {
        if (!this.drag || event.pointerId !== this.drag.pointerId) return false;
        const offset = this.drag.startX - event.clientX;
        this.drag.width = this.limit(
            this.drag.startWidth + offset / this.availableWidth,
        );
        return true;
    }

    private endDrag(commit: boolean) {
        const drag = this.drag;
        if (!drag) return;
        this.drag = undefined;
        if (this.frame !== undefined) {
            cancelAnimationFrame(this.frame);
            this.frame = undefined;
        }
        if (this.root.hasPointerCapture(drag.pointerId))
            this.root.releasePointerCapture(drag.pointerId);
        if (commit && drag.width !== drag.startWidth) this.save(drag.width);
        this.apply();
    }

    private onKeydown(event: KeyboardEvent) {
        if (event.key === "Escape" && this.drag) {
            event.preventDefault();
            event.stopPropagation();
            this.endDrag(false);
            return;
        }
        if (
            event.target !== this.separator ||
            !this.availableWidth ||
            this.drag
        )
            return;
        let width = this.limit(this.preferredWidth);
        const step = keyboardStep / this.availableWidth;
        switch (event.key) {
            case "ArrowLeft":
                width += step;
                break;
            case "ArrowRight":
                width -= step;
                break;
            case "Home":
                width = this.min;
                break;
            case "End":
                width = this.max;
                break;
            default:
                return;
        }
        event.preventDefault();
        this.save(this.limit(width));
        this.apply();
    }

    private save(width: number) {
        this.preferredWidth = width;
        try {
            if (width === defaultWidth) localStorage.removeItem(storageKey);
            else localStorage.setItem(storageKey, String(width));
        } catch {
            // Keep using the adjusted width within this page.
        }
    }
}
