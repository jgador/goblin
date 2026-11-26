const sections = ["connections", "agents", "timezone", "system"] as const;
const connectionProviders = ["codex", "github", "slack", "chatgpt"] as const;
export const settingsPanels = [...sections, ...connectionProviders] as const;
export type ConnectionProvider = (typeof connectionProviders)[number];
export type SettingsSelection = (typeof sections)[number] | ConnectionProvider;

export const settingsGroups: {
    label: string;
    items: { id: (typeof sections)[number]; label: string; glyph: string }[];
}[] = [
    {
        label: "Workspace",
        items: [
            { id: "connections", label: "Connections", glyph: "integrations" },
            { id: "agents", label: "Agents", glyph: "agents" },
        ],
    },
    {
        label: "Preferences",
        items: [{ id: "timezone", label: "Time & date", glyph: "clock" }],
    },
    {
        label: "Administration",
        items: [{ id: "system", label: "System", glyph: "activity" }],
    },
];

export function settingsSectionName(selection: SettingsSelection) {
    const section = isConnectionProvider(selection) ? "connections" : selection;
    return settingsGroups
        .flatMap(({ items }) => items)
        .find(({ id }) => id === section)!.label;
}

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
