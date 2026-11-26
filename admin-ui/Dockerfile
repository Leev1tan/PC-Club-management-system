# admin-ui - Next.js static export served by unprivileged nginx on port 3000
#   docker build -t ccms-admin-ui admin-ui
#   docker run --rm -p 3000:3000 -e API_UPSTREAM=http://<server-host>:8080 ccms-admin-ui
#
# The UI calls the API on its own origin (/api/...). This container proxies /api, /swagger and
# /health to API_UPSTREAM. Behind an ingress you can instead route /api straight to Cms.Server
# (path split: / -> admin-ui, /api -> server); the proxy is then simply unused.
#
# Build argument NEXT_PUBLIC_API_URL (optional): bake a different API origin into the UI.

FROM node:22-alpine AS build
WORKDIR /src
ENV NEXT_TELEMETRY_DISABLED=1

COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY . .
ARG NEXT_PUBLIC_API_URL=""
ENV NEXT_PUBLIC_API_URL=${NEXT_PUBLIC_API_URL}
# next.config.ts uses output: "export" -> static files in /src/out
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.27-alpine AS runtime

ENV API_UPSTREAM=http://server:8080 \
    NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1

COPY docker/default.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /src/out /usr/share/nginx/html

# nginx-unprivileged runs as user "nginx" (UID 101)
USER 101
EXPOSE 3000

HEALTHCHECK --interval=15s --timeout=5s --start-period=10s --retries=3 \
    CMD wget -q -O /dev/null http://127.0.0.1:3000/healthz || exit 1
