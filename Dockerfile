# Frontend, .NET restore, migration-tool publishing, and native downloads run
# in parallel.
# Node/npm stay in build stages.
FROM node:26.10.0-bookworm-slim AS node-base

WORKDIR /app

FROM node-base AS assets

COPY frontend/package.json frontend/package-lock.json ./frontend/

RUN --mount=type=cache,id=goblin-platform-npm-frontend,target=/root/.npm \
    npm --prefix frontend ci \
        --no-audit \
        --no-fund

COPY frontend ./frontend

RUN npm --prefix frontend run build

FROM node-base AS codex

COPY package.json package-lock.json ./

# The lockfile pins Codex and its platform-specific prebuilt Rust runtime.
RUN --mount=type=cache,id=goblin-platform-npm-codex,target=/root/.npm \
    npm ci \
        --omit=dev \
        --no-audit \
        --no-fund

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

FROM node-base AS github-cli

# Download the pinned GitHub CLI release and verify its checksum.
COPY deploy/install-gh.mjs /tmp/install-gh.mjs

RUN --mount=type=cache,id=goblin-platform-github-cli,target=/tmp/github-cli-cache,sharing=locked \
    node /tmp/install-gh.mjs

# Publish the app and migration tool against the shared .NET runtime.
# Only their publish outputs enter the final image; the SDK stays in build stages.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS dotnet-base

WORKDIR /app

COPY global.json ./
COPY backend/Directory.Build.props ./backend/Directory.Build.props

# Restore from project metadata so source and frontend changes reuse packages.
FROM dotnet-base AS restore

COPY backend/src/Goblin.Core/*.csproj ./backend/src/Goblin.Core/
COPY backend/src/Goblin.Contracts/*.csproj ./backend/src/Goblin.Contracts/
COPY backend/src/Goblin.Protocol/*.csproj ./backend/src/Goblin.Protocol/
COPY backend/src/Goblin.Persistence/*.csproj ./backend/src/Goblin.Persistence/
COPY backend/src/Goblin.Application/*.csproj ./backend/src/Goblin.Application/
COPY backend/src/Goblin.Execution/*.csproj ./backend/src/Goblin.Execution/
COPY backend/src/Goblin.Execution.Codex/*.csproj ./backend/src/Goblin.Execution.Codex/
COPY backend/src/Goblin.Integrations.Codex/*.csproj ./backend/src/Goblin.Integrations.Codex/
COPY backend/src/Goblin.Integrations.GitHub/*.csproj ./backend/src/Goblin.Integrations.GitHub/
COPY backend/src/Goblin.Integrations.Slack/*.csproj ./backend/src/Goblin.Integrations.Slack/
COPY backend/src/Goblin.Integrations.ChatGpt/*.csproj ./backend/src/Goblin.Integrations.ChatGpt/
COPY backend/src/Goblin.Web/*.csproj ./backend/src/Goblin.Web/

RUN --mount=type=cache,id=goblin-platform-nuget-http,target=/root/.local/share/NuGet/http-cache,sharing=locked \
    dotnet restore backend/src/Goblin.Web/Goblin.Web.csproj \
        --nologo

FROM restore AS publish

COPY backend/src ./backend/src
COPY backend/licenses ./backend/licenses
COPY docs/chatgpt-integration.md ./docs/chatgpt-integration.md

# Slack's icon is embedded in its assembly; browser output is copied at runtime.
COPY frontend/public/assets/branding/slack.png ./frontend/public/assets/branding/slack.png

# Compile and package once, including the SDK's gzip/Brotli assets and endpoint
# manifest. A separate build before publish repeats MSBuild's project traversal.
COPY --from=assets /app/frontend/dist ./frontend/dist

RUN dotnet publish backend/src/Goblin.Web/Goblin.Web.csproj \
        -c Release \
        -o /publish \
        --no-restore \
        --nologo \
    && mv /publish/wwwroot /browser \
    && mkdir /browser-metadata \
    && mv /publish/Goblin.Web.staticwebassets.endpoints.json /browser-metadata/

# Restore and publish independently of the web app. A separate download cache
# lets both restores run concurrently without waiting for the other's lock.
FROM dotnet-base AS database-restore

COPY backend/tools/Goblin.Database/*.csproj ./backend/tools/Goblin.Database/

RUN --mount=type=cache,id=goblin-platform-nuget-database-http,target=/root/.local/share/NuGet/http-cache,sharing=locked \
    dotnet restore backend/tools/Goblin.Database/Goblin.Database.csproj \
        --nologo

FROM database-restore AS database

COPY backend/tools/Goblin.Database ./backend/tools/Goblin.Database

RUN dotnet publish backend/tools/Goblin.Database/Goblin.Database.csproj \
        -c Release \
        -o /database \
        --no-restore \
        --nologo

# Runtime image for the application controller and isolated execution workers.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble AS runtime

WORKDIR /app

# Git serves the repository broker and workers; its package also brings Perl.
# These packages and their dependencies are in Ubuntu main. Keep its security
# and update channels while avoiding indexes for unused components.
RUN sed -i \
    's/^Components: .*/Components: main/' \
    /etc/apt/sources.list.d/ubuntu.sources

# Package indexes stay in the build cache, outside the runtime image.
RUN --mount=type=cache,id=goblin-platform-apt-lists,target=/var/lib/apt/lists,sharing=locked \
    apt-get update -o Acquire::Languages=none \
    && apt-get install -y --no-install-recommends \
        git \
        ca-certificates

COPY LICENSE NOTICE THIRD_PARTY_NOTICES.md /usr/share/doc/goblin/

# Keep large native layers ahead of frequently changing application files.
COPY --from=codex /codex /opt/codex
COPY --from=github-cli /githubcli/bin/gh /usr/local/bin/gh

# Repository workers use this wrapper to call Goblin's trusted GitHub broker.
COPY --chmod=755 deploy/goblin-github /usr/local/bin/goblin-github

RUN mkdir -p /data \
    && chown 1000:1000 /data

# Provisioning runs the SQL migrations with this separately published tool.
COPY --from=database --chown=1000:1000 /database /tools/database
COPY backend/database/migrations /migrations

# appsettings.json contains certificate paths; private keys are mounted at runtime.
COPY --from=publish --chown=1000:1000 /publish ./
COPY --from=publish --chown=1000:1000 /browser-metadata ./
COPY --from=publish --chown=1000:1000 /browser ./wwwroot

USER 1000:1000

# Resolve the native Codex executable and its bundled command-line helpers.
ENV PATH="/opt/codex/bin:/opt/codex/codex-path:${PATH}" \
    GOBLIN_HOST=0.0.0.0 \
    GOBLIN_PORT=8787 \
    GOBLIN_DATA_DIR=/data/platform \
    GOBLIN_PUBLIC_ORIGIN=http://localhost:8787

EXPOSE 8787

CMD ["dotnet", "Goblin.Web.dll"]
