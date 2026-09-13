FROM node:24.18.0-bookworm-slim AS assets

WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY tsconfig.json tsconfig.browser.json ./
COPY shared ./shared
COPY public ./public
COPY scripts ./scripts
RUN npm run build:assets
# Keep the official native runtime's resources adjacent to its binary.
RUN node --input-type=module -e 'import { cpSync } from "node:fs"; const arch = process.arch; const target = arch === "arm64" ? "aarch64-unknown-linux-musl" : "x86_64-unknown-linux-musl"; cpSync(`node_modules/@openai/codex-linux-${arch}/vendor/${target}`, "/codex", { recursive: true });'

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /app
COPY global.json Directory.Build.props ./
COPY src ./src
COPY --from=assets /app/dist/public ./dist/public
RUN dotnet publish src/Goblin.Web/Goblin.Web.csproj -c Release -o /publish --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
WORKDIR /app
COPY --from=build /publish ./
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
