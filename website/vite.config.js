import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";

const installationAssets = resolve(
    import.meta.dirname,
    "../.artifacts/installation-assets/output",
);

export default {
    plugins: [
        {
            name: "installation-preview",
            configureServer(server) {
                server.middlewares.use((request, response, next) => {
                    const path = new URL(request.url, "http://localhost")
                        .pathname;
                    const match = path.match(
                        /^\/install\/(azure\/(?:releases\.json|versions\/(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-preview\.[1-9]\d*)?\/(?:azuredeploy\.json|azuredeploy\.portal\.json|createUiDefinition\.json|release\.json|SHA256SUMS)))$/,
                    );
                    if (
                        !match ||
                        !existsSync(
                            resolve(installationAssets, "azure/releases.json"),
                        )
                    )
                        return next();
                    try {
                        const bytes = readFileSync(
                            resolve(installationAssets, match[1]),
                        );
                        response.setHeader(
                            "Content-Type",
                            path.endsWith(".json")
                                ? "application/json"
                                : "text/plain",
                        );
                        response.setHeader("Cache-Control", "no-store");
                        response.end(bytes);
                    } catch {
                        response.statusCode = 404;
                        response.end("Installation asset unavailable");
                    }
                });
            },
        },
    ],
    // Keep the source mount read-only; dependencies live in the development image.
    cacheDir: "/tmp/goblin-website-vite",
    // This static website does not consume environment configuration.
    envDir: false,
    envPrefix: [],
    server: {
        host: "0.0.0.0",
        port: 5173,
        strictPort: true,
    },
};
