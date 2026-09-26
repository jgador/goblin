import { resolve } from "node:path";

// npm build compiles this binary. Production consumes the separately released archive.
export const goblinctl = resolve(
    process.env.GOBLINCTL_TEST_BINARY ?? "target/debug/goblinctl",
);
