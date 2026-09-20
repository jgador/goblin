import { copyFile, cp, mkdir, readFile, writeFile } from "node:fs/promises";

const root = new URL("../", import.meta.url);
const output = new URL("dist/", root);
await cp(new URL("public/", root), output, { recursive: true });
for (const page of ["connection", "work"]) {
    await mkdir(new URL(`${page}/`, output), { recursive: true });
    for (const name of ["index.html", "styles.css"]) {
        await copyFile(
            new URL(`src/${page}/${name}`, root),
            new URL(`${page}/${name}`, output),
        );
    }
}

// First-time setup and Settings render the same Codex markup and controller.
const connectionHtml = await readFile(
    new URL("src/connection/index.html", root),
    "utf8",
);
const panel = connectionHtml.slice(
    connectionHtml.indexOf('      <div class="card"'),
    connectionHtml.indexOf('      <p class="below-card"'),
);
const help = connectionHtml.slice(
    connectionHtml.indexOf("    <template"),
    connectionHtml.indexOf("    <footer"),
);
await writeFile(
    new URL("connection/panel.html", output),
    '<button id="lock-button" hidden>Lock workspace</button>' + panel + help,
);
await mkdir(new URL("settings/", output), { recursive: true });
const connectionStyles = await readFile(
    new URL("src/connection/styles.css", root),
    "utf8",
);
const settingsStyles = await readFile(
    new URL("src/settings/styles.css", root),
    "utf8",
);
await writeFile(
    new URL("settings/styles.css", output),
    "@scope (.codex-settings) {\n" +
        connectionStyles +
        "\n}\n" +
        settingsStyles,
);
