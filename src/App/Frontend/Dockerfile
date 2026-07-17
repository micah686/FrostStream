# FrostStream Frontend (static Svelte SPA served by Caddy).
# Build from the src/ directory:  docker build -f App/Frontend/Dockerfile -t froststream/frontend .
#
# The only runtime setting is WEBAPI_UPSTREAM (default http://webapi:8080).

FROM node:22-alpine AS build
ENV COREPACK_ENABLE_DOWNLOAD_PROMPT=0
RUN corepack enable
WORKDIR /app

# The packageManager field in package.json pins the pnpm version corepack activates.
COPY App/Frontend/package.json App/Frontend/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile

COPY App/Frontend/ .
RUN pnpm build

FROM caddy:2.10-alpine
COPY --from=build /app/build /srv
COPY App/Frontend/Caddyfile /etc/caddy/Caddyfile

ENV WEBAPI_UPSTREAM=http://webapi:8080
EXPOSE 25000

HEALTHCHECK --interval=10s --timeout=3s --start-period=5s --retries=5 \
  CMD wget -qO- http://127.0.0.1:25000/healthz || exit 1
