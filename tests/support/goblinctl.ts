import { environmentVariables as Env } from "../../config/environment.mjs";
import { resolve } from "node:path";

// make build compiles this binary. Production consumes the separately released archive.
export const goblinctl = resolve(
    process.env[Env.GOBLINCTL_TEST_BINARY.name] ?? "target/debug/goblinctl",
);

// This library harness is built only for offline tests and is never packaged.
export const databaseHostFixture = resolve(
    "target/debug/examples/host_fixture",
);
