FROM node:26.10.0-bookworm-slim AS development
WORKDIR /opt/website-dev
COPY website/package.json website/package-lock.json ./
RUN npm ci --no-audit --no-fund
RUN mkdir -p /.artifacts/installation-assets/output
WORKDIR /app
USER node
EXPOSE 5173
CMD ["/opt/website-dev/node_modules/.bin/vite", "--configLoader", "native"]

FROM node:26.10.0-bookworm-slim AS installation
WORKDIR /build
COPY scripts/installation-assets.mts scripts/installation-assets.mts
COPY website/package-installation.mts website/package-installation.mts
COPY website/install/azure/releases.json website/install/azure/releases.json
# The ignored snapshot is supplied by Website Release. Local builds start empty.
COPY .artifacts/ /inputs/
RUN node website/package-installation.mts /inputs /installation

# The deployed site is buildless: preserve its HTML, CSS, JavaScript, and assets.
FROM nginx:1.30.5-alpine AS production
COPY website/nginx.conf /etc/nginx/nginx.conf
COPY website/index.html website/website.css website/website.js /usr/share/nginx/html/
COPY website/assets/ /usr/share/nginx/html/assets/
COPY website/install/ /usr/share/nginx/html/install/
COPY --from=installation /installation/ /usr/share/nginx/html/install/
COPY LICENSE NOTICE THIRD_PARTY_NOTICES.md /usr/share/doc/goblin-website/
USER 101:101
EXPOSE 8080
ENTRYPOINT ["nginx"]
CMD ["-g", "daemon off;"]
