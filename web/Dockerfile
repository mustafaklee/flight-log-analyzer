# syntax=docker/dockerfile:1
# The web client: a static build served by nginx, which also proxies /api and /hubs to the API so the browser
# talks to a single origin (ADR-0010). Build context: web/.

FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
# Map tiles are fetched by the browser; the URL is baked into the build (default OpenStreetMap).
ARG VITE_MAP_TILE_URL=
ARG VITE_MAP_TILE_ATTRIBUTION=
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.29-alpine AS runtime
COPY nginx.conf /etc/nginx/templates/default.conf.template
COPY --from=build /app/dist /usr/share/nginx/html
# Values substituted into the template at start-up.
ENV API_UPSTREAM=http://api:8080 \
    MAP_TILE_ORIGIN=https://tile.openstreetmap.org
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --retries=5 CMD wget -q -O /dev/null http://127.0.0.1:8080/healthz || exit 1
