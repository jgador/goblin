import { timeZonePlaces } from "./timezone-places.js";

type Preferences = { timeZone: string | null };
let current: Preferences | undefined;
const countryNames = new Intl.DisplayNames("en", { type: "region" });

export function timeZoneName(zone: string): string {
    if (zone === "UTC") return "UTC — Coordinated Universal Time";
    const place = timeZonePlaces[zone];
    return place
        ? `${countryNames.of(place[0]) ?? place[0]} — ${place[1]}`
        : zone.replaceAll("_", " ");
}

export function validTimeZone(value: unknown): value is string {
    if (typeof value !== "string" || !value || value.length > 100) return false;
    try {
        new Intl.DateTimeFormat(undefined, { timeZone: value });
        return value === "UTC" || value.includes("/");
    } catch {
        return false;
    }
}

export type TimeZoneSuggestion = {
    timeZone: string;
    source: "ip" | "browser" | "utc";
};

export async function locateTimeZone(
    signal: AbortSignal,
): Promise<TimeZoneSuggestion> {
    try {
        // Request from the visitor's browser so deployment geography is irrelevant.
        // Only the timezone is requested; no Goblin credentials or referrer are sent.
        const response = await fetch(
            "https://ipwho.is/?fields=success,timezone.id",
            {
                credentials: "omit",
                referrerPolicy: "no-referrer",
                cache: "no-store",
                redirect: "error",
                signal: AbortSignal.any([signal, AbortSignal.timeout(3000)]),
            },
        );
        if (response.ok) {
            const result = await response.json();
            signal.throwIfAborted();
            if (result?.success === true && validTimeZone(result.timezone?.id))
                return { timeZone: result.timezone.id, source: "ip" };
        }
    } catch {
        // Offline, blocked, rate-limited, or invalid lookups use local detection.
    }
    signal.throwIfAborted();
    const browser = suggestedTimeZone();
    return browser
        ? { timeZone: browser, source: "browser" }
        : { timeZone: "UTC", source: "utc" };
}

export function suggestedTimeZone(): string | undefined {
    try {
        const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
        return validTimeZone(zone) ? zone : undefined;
    } catch {
        return undefined;
    }
}

export const savedTimeZone = () => current?.timeZone;
export const displayTimeZone = () =>
    current?.timeZone ?? suggestedTimeZone() ?? "UTC";
export function resetTimeZone() {
    current = undefined;
}

function accept(value: Preferences) {
    if (value?.timeZone !== null && !validTimeZone(value?.timeZone))
        throw new Error("Workspace timezone could not be loaded.");
    const changed = current?.timeZone !== value.timeZone;
    current = value;
    if (changed) window.dispatchEvent(new Event("goblin-timezone-changed"));
    return value;
}

export async function loadTimeZone(signal?: AbortSignal) {
    const response = await fetch("/api/preferences", {
        cache: "no-store",
        signal,
    });
    if (!response.ok)
        throw new Error("Workspace timezone could not be loaded. Try again.");
    const result = await response.json();
    signal?.throwIfAborted();
    return accept(result);
}

export async function saveTimeZone(
    timeZone: string,
    expectedTimeZone: string | null,
    signal: AbortSignal,
) {
    const response = await fetch("/api/preferences/timezone", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ timeZone, expectedTimeZone }),
        signal,
    });
    const result = await response.json();
    signal.throwIfAborted();
    if (!response.ok)
        throw new Error(
            result.error?.message ??
                "The timezone could not be saved. Try again.",
        );
    return accept(result);
}

export function formatTimestamp(
    value: string,
    options: Intl.DateTimeFormatOptions = {},
) {
    const date = new Date(value);
    if (!Number.isFinite(date.getTime())) return "Time unavailable";
    return new Intl.DateTimeFormat(undefined, {
        year: "numeric",
        month: "short",
        day: "numeric",
        hour: "numeric",
        minute: "2-digit",
        second: "2-digit",
        timeZoneName: "short",
        ...options,
        timeZone: displayTimeZone(),
    }).format(date);
}
