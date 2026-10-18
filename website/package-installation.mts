import { cpSync, existsSync, mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { validateAssets } from "../scripts/installation-assets.mts";

const [input, output] = process.argv.slice(2);
if (!input || !output)
    throw new Error("Pass installation input and output directories");
const source = existsSync(resolve(input, "installation-assets/output"))
    ? resolve(input, "installation-assets/output")
    : resolve("website/install");
validateAssets(source);
mkdirSync(resolve(output, "azure"), { recursive: true });
cpSync(
    resolve(source, "azure/releases.json"),
    resolve(output, "azure/releases.json"),
);
if (existsSync(resolve(source, "azure/versions")))
    cpSync(
        resolve(source, "azure/versions"),
        resolve(output, "azure/versions"),
        { recursive: true },
    );
