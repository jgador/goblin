FROM node:26.10.0-bookworm-slim AS development
WORKDIR /opt/website-dev
COPY website/package.json website/package-lock.json ./
RUN npm ci --no-audit --no-fund
WORKDIR /app
USER node
EXPOSE 5173
CMD ["/opt/website-dev/node_modules/.bin/vite", "--configLoader", "native"]

# The deployed site is buildless: preserve its HTML, CSS, JavaScript, and assets.
FROM nginx:1.30.5-alpine AS production
COPY website/nginx.conf /etc/nginx/nginx.conf
COPY website/index.html website/website.css website/website.js /usr/share/nginx/html/
COPY website/assets/ /usr/share/nginx/html/assets/
COPY LICENSE NOTICE THIRD_PARTY_NOTICES.md /usr/share/doc/goblin-website/
USER 101:101
EXPOSE 8080
ENTRYPOINT ["nginx"]
CMD ["-g", "daemon off;"]
