import { copyFile, cp, mkdir, readFile, writeFile } from "node:fs/promises";

const root = new URL("../", import.meta.url);
const output = new URL("dist/", root);
const controls = await readFile(new URL("src/controls.css", root), "utf8");
await cp(new URL("public/", root), output, { recursive: true });
for (const page of ["connection", "work"]) {
    await mkdir(new URL(`${page}/`, output), { recursive: true });
    await copyFile(
        new URL(`src/${page}/index.html`, root),
        new URL(`${page}/index.html`, output),
    );
    await writeFile(
        new URL(`${page}/styles.css`, output),
        controls +
            "\n" +
            (await readFile(new URL(`src/${page}/styles.css`, root), "utf8")),
    );
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
