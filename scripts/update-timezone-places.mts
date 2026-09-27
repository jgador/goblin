import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { format } from "prettier";

// Use an installed IANA tzdb directory; builds use the checked-in result.
const directory = process.argv[2];
if (!directory)
    throw new Error(
        "Usage: node scripts/update-timezone-places.mts <zoneinfo-directory>",
    );
const [table, data] = await Promise.all([
    readFile(join(directory, "zone.tab"), "utf8"),
    readFile(join(directory, "tzdata.zi"), "utf8"),
]);
const version = /^# version (\S+)/m.exec(data)?.[1];
if (!version) throw new Error("The IANA timezone database version is missing.");
const places = new Map<string, [string, string]>();
for (const line of table.split("\n")) {
    if (!line || line.startsWith("#")) continue;
    const [country, , zone] = line.split("\t");
    if (!/^[A-Z]{2}$/.test(country) || !zone?.includes("/"))
        throw new Error("Invalid zone.tab entry.");
    // Keep each place's country even when its clocks agree with another country.
    places.set(zone, [
        country,
        zone.split("/").slice(1).join(" / ").replaceAll("_", " "),
    ]);
}
const links = [...data.matchAll(/^L\s+(\S+)\s+(\S+)$/gm)];
let previousSize: number;
do {
    previousSize = places.size;
    for (const [, target, alias] of links) {
        const place = places.get(target);
        if (place && alias.includes("/") && !places.has(alias))
            places.set(alias, place);
    }
} while (places.size !== previousSize);
const source = `// Generated from the public-domain IANA tzdb ${version}: zone.tab and tzdata.zi.
// https://data.iana.org/time-zones/releases/tzdata${version}.tar.gz
// Regenerate with: node scripts/update-timezone-places.mts /usr/share/zoneinfo
// zone.tab preserves a country and city for each selectable geographic zone.
export const timeZonePlaces: Readonly<Record<string, readonly [country: string, city: string]>> = ${JSON.stringify(Object.fromEntries([...places].sort(([a], [b]) => a.localeCompare(b, "en"))))};
`;
await writeFile(
    new URL("../frontend/src/settings/timezone-places.ts", import.meta.url),
    await format(source, { parser: "typescript", tabWidth: 4 }),
);
console.log(
    `Updated ${places.size} timezone places from IANA tzdb ${version}.`,
);
