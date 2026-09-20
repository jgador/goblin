import { copyFile, cp, mkdir } from "node:fs/promises";

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
