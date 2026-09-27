import { escapeHtml as e } from "../work/presentation.js";
import {
    loadTimeZone,
    locateTimeZone,
    savedTimeZone,
    saveTimeZone,
    suggestedTimeZone,
    timeZoneName,
    type TimeZoneSuggestion,
    validTimeZone,
} from "./timezone.js";

export function mountTimeZonePicker(
    panel: HTMLElement,
    mode: "setup" | "settings",
    onSaved?: () => void,
) {
    const controller = new AbortController();
    const { signal } = controller;
    let expected: string | null;
    let zones: string[] = [];
    let selected = "UTC";
    let suggestion: TimeZoneSuggestion | undefined;
    let lookupAttempted = false;
    let locating = false;
    let saving = false;
    let ready = false;
    panel.innerHTML = `<h3>${mode === "setup" ? "Choose your timezone" : "Time & date"}</h3>
        <p class="settings-description">Choose the timezone for everyone using this workspace.</p>
        <form class="timezone-form">
            <p class="settings-description" data-timezone-suggestion aria-live="polite">Loading timezone suggestion…</p>
            <div class="settings-actions"><button type="button" data-timezone-detect disabled>Detect from IP</button><button type="button" data-timezone-use hidden>Use suggestion</button></div>
            <p class="settings-description">IP location uses <a href="https://ipwhois.io/documentation" target="_blank" rel="noopener noreferrer">ipwho.is</a> to look up your public IP. A VPN can suggest the VPN server’s timezone.</p>
            <label for="timezone-search">Find a timezone</label>
            <input class="field-control" id="timezone-search" type="search" placeholder="Search country or city, e.g. Philippines or Manila" autocomplete="off" aria-describedby="timezone-matches" disabled>
            <p class="settings-description" id="timezone-matches" role="status" hidden></p>
            <label for="timezone-select">Timezone</label>
            <select class="field-control select-control" id="timezone-select" required disabled aria-describedby="timezone-preview timezone-help"><button type="button"><selectedcontent></selectedcontent></button></select>
            <p class="settings-description" id="timezone-preview" aria-live="polite"></p>
            <p class="settings-description" id="timezone-help">Daylight saving changes apply automatically.${mode === "setup" ? " You can change this later in Settings → Time & date." : " Dates and log times use this choice on every browser and device."}</p>
            <p class="settings-notice" data-timezone-error role="alert" hidden></p>
            <p class="settings-description" data-timezone-status role="status">Loading timezones…</p>
            <div class="settings-actions"><button class="settings-primary" type="submit" disabled>${mode === "setup" ? "Use timezone" : "Save timezone"}</button><button type="button" data-timezone-retry hidden>Reload settings</button></div>
        </form>`;
    const form = panel.querySelector("form")!;
    const search = panel.querySelector<HTMLInputElement>("#timezone-search")!;
    const select = panel.querySelector<HTMLSelectElement>("#timezone-select")!;
    const submit = panel.querySelector<HTMLButtonElement>('[type="submit"]')!;
    const retry = panel.querySelector<HTMLButtonElement>(
        "[data-timezone-retry]",
    )!;
    const error = panel.querySelector<HTMLElement>("[data-timezone-error]")!;
    const status = panel.querySelector<HTMLElement>("[data-timezone-status]")!;
    const preview = panel.querySelector<HTMLElement>("#timezone-preview")!;
    const matchesStatus =
        panel.querySelector<HTMLElement>("#timezone-matches")!;
    const suggestionText = panel.querySelector<HTMLElement>(
        "[data-timezone-suggestion]",
    )!;
    const detect = panel.querySelector<HTMLButtonElement>(
        "[data-timezone-detect]",
    )!;
    const useSuggestion = panel.querySelector<HTMLButtonElement>(
        "[data-timezone-use]",
    )!;
    const now = new Date();
    const labels = new Map<string, string>();
    const label = (zone: string) => {
        const cached = labels.get(zone);
        if (cached) return cached;
        const offset = new Intl.DateTimeFormat("en", {
            timeZone: zone,
            timeZoneName: "longOffset",
        })
            .formatToParts(now)
            .find((part) => part.type === "timeZoneName")!
            .value.replace("GMT", "UTC");
        const text =
            zone === "UTC"
                ? timeZoneName(zone)
                : `${timeZoneName(zone)} (${offset})`;
        labels.set(zone, text);
        return text;
    };
    const normalize = (text: string) =>
        text
            .normalize("NFD")
            .replace(/\p{M}/gu, "")
            .toLowerCase()
            .replace(/[\/_—(),]/g, " ");
    const searchable = new Map<string, string>();
    function controls() {
        search.disabled = select.disabled = submit.disabled = !ready || saving;
        detect.disabled = useSuggestion.disabled = !ready || saving || locating;
    }
    function showSuggestion() {
        if (locating) {
            suggestionText.textContent =
                "Detecting timezone from your IP location…";
        } else if (suggestion) {
            const prefix =
                suggestion.source === "ip"
                    ? "Your IP location suggests"
                    : "Your browser suggests";
            suggestionText.innerHTML =
                suggestion.source === "utc"
                    ? `${lookupAttempted ? "Location and browser detection were unavailable." : "Your browser did not report a timezone."} UTC is selected as a starting point when no workspace timezone is saved.`
                    : `${lookupAttempted && suggestion.source === "browser" ? "IP location was unavailable. " : ""}${prefix} <strong>${e(label(suggestion.timeZone))}</strong>. You can choose any timezone below.`;
        }
        useSuggestion.hidden =
            locating || !suggestion || suggestion.timeZone === selected;
        controls();
    }
    async function lookup() {
        locating = true;
        lookupAttempted = true;
        showSuggestion();
        try {
            suggestion = await locateTimeZone(signal);
        } finally {
            locating = false;
            if (!signal.aborted) showSuggestion();
        }
    }
    function options() {
        const query = normalize(search.value).trim();
        const terms = query.split(/\s+/).filter(Boolean);
        const matches = zones.filter((zone) =>
            terms.every((term) => searchable.get(zone)!.includes(term)),
        );
        const option = (zone: string) =>
            `<option value="${e(zone)}" ${zone === selected ? "selected" : ""}>${e(label(zone))}</option>`;
        select.innerHTML =
            '<button type="button"><selectedcontent></selectedcontent></button>' +
            (matches.includes(selected)
                ? matches.map(option).join("")
                : `<optgroup label="Current selection">${option(selected)}</optgroup>${matches.length ? `<optgroup label="Search results">${matches.map(option).join("")}</optgroup>` : ""}`);
        matchesStatus.hidden = !query;
        matchesStatus.textContent = !matches.length
            ? "No matching timezones. Try a country or city, such as Philippines or Manila. Your selection is kept."
            : `${matches.length} matching timezone${matches.length === 1 ? "" : "s"}. Open the Timezone menu to choose.`;
    }
    function updatePreview() {
        preview.textContent =
            "Current time: " +
            new Intl.DateTimeFormat(undefined, {
                timeZone: selected,
                dateStyle: "full",
                timeStyle: "long",
            }).format(new Date());
    }
    function failure(reason: unknown) {
        error.textContent =
            reason instanceof Error
                ? reason.message
                : "The timezone could not be saved. Try again.";
        error.hidden = false;
    }
    async function load() {
        error.hidden = true;
        retry.hidden = true;
        ready = false;
        controls();
        status.textContent = "Loading timezones…";
        try {
            const [preferences, response] = await Promise.all([
                loadTimeZone(signal),
                fetch("/api/preferences/timezones", { signal }),
            ]);
            if (!response.ok)
                throw new Error("Timezones could not be loaded. Try again.");
            const available: unknown = await response.json();
            if (
                !Array.isArray(available) ||
                !available.every((zone) => typeof zone === "string")
            )
                throw new Error("Timezones could not be loaded. Try again.");
            expected = preferences.timeZone;
            const browser = suggestedTimeZone();
            suggestion = browser
                ? { timeZone: browser, source: "browser" }
                : { timeZone: "UTC", source: "utc" };
            if (expected === null) await lookup();
            signal.throwIfAborted();
            selected = expected ?? suggestion.timeZone;
            zones = [...new Set(["UTC", ...available, selected])]
                .filter(validTimeZone)
                .sort((a, b) => label(a).localeCompare(label(b), "en"));
            for (const zone of zones)
                searchable.set(zone, normalize(`${label(zone)} ${zone}`));
            options();
            updatePreview();
            ready = true;
            status.textContent = "";
            showSuggestion();
        } catch (reason) {
            if (signal.aborted) return;
            status.textContent = "";
            failure(reason);
            retry.hidden = false;
        }
    }
    detect.addEventListener("click", () => void lookup().catch(() => {}), {
        signal,
    });
    useSuggestion.addEventListener(
        "click",
        () => {
            if (!suggestion || useSuggestion.disabled) return;
            selected = suggestion.timeZone;
            if (!zones.includes(selected)) {
                zones.push(selected);
                zones.sort((a, b) => label(a).localeCompare(label(b), "en"));
                searchable.set(
                    selected,
                    normalize(`${label(selected)} ${selected}`),
                );
            }
            search.value = "";
            options();
            updatePreview();
            showSuggestion();
            error.hidden = true;
            status.textContent =
                "Suggestion selected. Save the timezone to apply it to this workspace.";
        },
        { signal },
    );
    search.addEventListener("input", options, { signal });
    select.addEventListener(
        "change",
        () => {
            selected = select.value;
            options();
            updatePreview();
            showSuggestion();
            error.hidden = true;
            status.textContent = "";
        },
        { signal },
    );
    retry.addEventListener("click", () => void load(), { signal });
    form.addEventListener(
        "submit",
        async (event) => {
            event.preventDefault();
            if (submit.disabled) return;
            saving = true;
            controls();
            error.hidden = true;
            status.textContent = "Saving timezone…";
            try {
                await saveTimeZone(selected, expected, signal);
                expected = savedTimeZone()!;
                status.textContent =
                    "Timezone saved for this workspace. Reopen or refresh any open log views to apply it.";
                onSaved?.();
            } catch (reason) {
                if (signal.aborted) return;
                status.textContent = "";
                failure(reason);
                retry.hidden = false;
            } finally {
                saving = false;
                if (!signal.aborted) controls();
            }
        },
        { signal },
    );
    void load();
    return () => controller.abort();
}

export class TimeZoneSetup {
    private readonly dialog = document.createElement("dialog");
    private dispose?: () => void;
    constructor() {
        this.dialog.className = "settings-dialog timezone-setup";
        this.dialog.setAttribute("aria-label", "Choose your timezone");
        this.dialog.addEventListener("cancel", (event) =>
            event.preventDefault(),
        );
        document.body.append(this.dialog);
    }
    open(onSaved: () => void) {
        if (this.dialog.open) return;
        this.dialog.innerHTML =
            '<div class="settings-content"><section></section></div>';
        this.dispose = mountTimeZonePicker(
            this.dialog.querySelector("section")!,
            "setup",
            () => {
                this.reset();
                onSaved();
            },
        );
        this.dialog.showModal();
    }
    reset() {
        this.dispose?.();
        this.dispose = undefined;
        this.dialog.close();
        this.dialog.replaceChildren();
    }
}
