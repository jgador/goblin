const sections = [
    "connections",
    "agents",
    "timezone",
    "system",
    "cluster",
    "logs",
] as const;
const connectionProviders = ["codex", "github", "slack", "chatgpt"] as const;
export const settingsPanels = [...sections, ...connectionProviders] as const;
export type ConnectionProvider = (typeof connectionProviders)[number];
export type SettingsSelection = (typeof sections)[number] | ConnectionProvider;

export function isConnectionProvider(
    value: string,
): value is ConnectionProvider {
    return connectionProviders.some((provider) => provider === value);
}

export function settingsTarget(value: string): SettingsSelection {
    return isConnectionProvider(value)
        ? value
        : (sections.find((section) => section === value) ?? "connections");
}

export function settingsSelection(params: URLSearchParams): SettingsSelection {
    const section = params.get("section") ?? "connections";
    if (section === "connections") {
        const provider = params.get("provider") ?? "";
        return isConnectionProvider(provider) ? provider : "connections";
    }
    return sections.find((value) => value === section) ?? "connections";
}

export function settingsHref(selection: SettingsSelection): string {
    return isConnectionProvider(selection)
        ? `/settings?section=connections&provider=${selection}`
        : `/settings?section=${selection}`;
}
