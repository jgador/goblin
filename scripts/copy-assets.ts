import { copyFile, mkdir } from "node:fs/promises";

const output = new URL("../public/", import.meta.url);
for (const name of ["index.html", "styles.css", "assets/branding/icon.svg", "work/index.html", "work/styles.css"]) {
  const destination = new URL(name, output);
  await mkdir(new URL(".", destination), { recursive: true });
  await copyFile(new URL(`../../public/${name}`, import.meta.url), destination);
}
