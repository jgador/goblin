# Build frontend assets and stage native CLI tools; Node/npm stay in this stage.
FROM node:24.18.0-bookworm-slim AS assets

WORKDIR /app
COPY package.json package-lock.json ./
COPY frontend/package.json ./frontend/package.json
# The lockfile pins Codex and its platform-specific prebuilt Rust runtime.
RUN npm ci
COPY frontend ./frontend
RUN npm run build:assets
# Download the pinned GitHub CLI release and verify its checksum.
COPY deploy/install-gh.mjs /tmp/install-gh.mjs
RUN node /tmp/install-gh.mjs
# C# launches the native binary as `codex app-server` and uses JSON-RPC over stdio.
# Keep the runtime's helpers and resources adjacent to its binary. Repository
# execution needs codex-code-mode-host even when the code_mode feature is off.
# Goblin does not use voice, but excluding its resources needs runtime validation.
# Any pruning belongs here, before the final COPY; deleting files in a later
# image layer would leave their bytes in the earlier layer.
RUN node --input-type=module <<'JS'
import { cpSync } from "node:fs";

// Match npm's architecture name to the native bundle's target directory.
const arch = process.arch;
const target = arch === "arm64"
    ? "aarch64-unknown-linux-musl"
    : "x86_64-unknown-linux-musl";

cpSync(
    `node_modules/@openai/codex-linux-${arch}/vendor/${target}`,
    "/codex",
    { recursive: true },
);
JS

# Publish the app and migration tool against the shared .NET runtime.
# Only their publish outputs enter the final image; the SDK stays in this stage.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /app
COPY global.json ./
COPY backend/Directory.Build.props ./backend/Directory.Build.props
COPY backend/src ./backend/src
COPY backend/tools ./backend/tools
COPY --from=assets /app/frontend/dist ./frontend/dist
RUN dotnet publish backend/src/Goblin.Web/Goblin.Web.csproj -c Release -o /publish --nologo
RUN dotnet publish backend/tools/Goblin.Database/Goblin.Database.csproj -c Release -o /database --nologo

# Runtime image for the application controller and isolated execution workers.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
WORKDIR /app
# Git serves the repository broker and workers; its package also brings Perl.
# Remove package indexes in the same layer to avoid retaining download metadata.
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates && rm -rf /var/lib/apt/lists/*
# appsettings.json contains certificate paths; private keys are mounted at runtime.
COPY --from=build --chown=1000:1000 /publish ./
# Provisioning runs the SQL migrations with this separately published tool.
COPY --from=build --chown=1000:1000 /database /tools/database
COPY backend/database/migrations /migrations
COPY --from=assets /codex /opt/codex
COPY --from=assets /githubcli/bin/gh /usr/local/bin/gh
# Repository workers use this wrapper to call Goblin's trusted GitHub broker.
COPY --chmod=755 deploy/goblin-github /usr/local/bin/goblin-github
RUN mkdir -p /data && chown 1000:1000 /data

USER 1000:1000
# Resolve the native Codex executable and its bundled command-line helpers.
ENV PATH="/opt/codex/bin:/opt/codex/codex-path:${PATH}" \
    GOBLIN_HOST=0.0.0.0 \
    GOBLIN_PORT=8787 \
    GOBLIN_DATA_DIR=/data/auth \
    GOBLIN_PUBLIC_ORIGIN=http://localhost:8787
EXPOSE 8787
CMD ["dotnet", "Goblin.Web.dll"]
