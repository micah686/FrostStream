# FrostStream Frontend (SvelteKit BFF, adapter-node).
# Build from the src/ directory:  docker build -f App/Frontend/Dockerfile -t froststream/frontend .
#
# Runtime configuration is read from the environment by the node server ($env/dynamic/private):
#   API_BASE_URL, AUTH_AUTHORITY, AUTH_CLIENT_ID, AUTH_CLIENT_SECRET, AUTH_SCOPES,
#   SINGLE_USER_MODE, PORT (default 25000).

FROM node:22-alpine AS build
ENV COREPACK_ENABLE_DOWNLOAD_PROMPT=0
RUN corepack enable
WORKDIR /app

# The packageManager field in package.json pins the pnpm version corepack activates.
COPY App/Frontend/package.json App/Frontend/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile

COPY App/Frontend/ .
RUN pnpm build && pnpm prune --prod

FROM node:22-alpine
WORKDIR /app
COPY --from=build /app/build ./build
COPY --from=build /app/node_modules ./node_modules
COPY --from=build /app/package.json .

ENV NODE_ENV=production \
    PORT=25000
EXPOSE 3000

CMD ["node", "build"]
