import { copyFile, mkdir } from "node:fs/promises";

const output = new URL("../public/", import.meta.url);
await mkdir(output, { recursive: true });
for (const name of ["index.html", "styles.css", "icon.svg"]) {
  await copyFile(new URL(`../../public/${name}`, import.meta.url), new URL(name, output));
}
