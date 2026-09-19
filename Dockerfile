FROM node:24.18.0-bookworm-slim AS assets

WORKDIR /app
COPY package.json package-lock.json ./
COPY frontend/package.json ./frontend/package.json
RUN npm ci
COPY frontend ./frontend
RUN npm run build:assets
# Keep the official native runtime's resources adjacent to its binary.
RUN node --input-type=module -e 'import { cpSync } from "node:fs"; const arch = process.arch; const target = arch === "arm64" ? "aarch64-unknown-linux-musl" : "x86_64-unknown-linux-musl"; cpSync(`node_modules/@openai/codex-linux-${arch}/vendor/${target}`, "/codex", { recursive: true });'

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /app
COPY global.json ./
COPY backend/Directory.Build.props ./backend/Directory.Build.props
COPY backend/src ./backend/src
COPY backend/tools ./backend/tools
COPY --from=assets /app/frontend/dist ./frontend/dist
RUN dotnet publish backend/src/Goblin.Web/Goblin.Web.csproj -c Release -o /publish --nologo
RUN dotnet publish backend/tools/Goblin.Database/Goblin.Database.csproj -c Release -o /database --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates && rm -rf /var/lib/apt/lists/*
# appsettings.json contains certificate paths; private keys are mounted at runtime.
COPY --from=build --chown=1000:1000 /publish ./
COPY --from=build --chown=1000:1000 /database /tools/database
COPY backend/database/migrations /migrations
COPY --from=assets /codex /opt/codex
RUN mkdir -p /data && chown 1000:1000 /data

USER 1000:1000
ENV PATH="/opt/codex/bin:/opt/codex/codex-path:${PATH}" \
    GOBLIN_HOST=0.0.0.0 \
    GOBLIN_PORT=8787 \
    GOBLIN_DATA_DIR=/data/auth \
    GOBLIN_PUBLIC_ORIGIN=http://localhost:8787
EXPOSE 8787
CMD ["dotnet", "Goblin.Web.dll"]
