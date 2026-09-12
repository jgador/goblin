FROM node:24.18.0-bookworm-slim AS build

WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY tsconfig.json tsconfig.browser.json ./
COPY src ./src
COPY shared ./shared
COPY public ./public
COPY scripts ./scripts
RUN npm run build

FROM node:24.18.0-bookworm-slim

WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --omit=dev && npm cache clean --force
COPY --from=build --chown=node:node /app/dist/src ./dist/src
COPY --from=build --chown=node:node /app/dist/public ./dist/public
RUN mkdir -p /data && chown node:node /data

USER node
ENV NODE_ENV=production \
    GOBLIN_HOST=0.0.0.0 \
    GOBLIN_PORT=8787 \
    GOBLIN_DATA_DIR=/data/auth \
    GOBLIN_PUBLIC_ORIGIN=http://localhost:8787
EXPOSE 8787
CMD ["node", "dist/src/main.js"]
