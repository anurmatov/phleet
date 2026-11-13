# Pinned to the releases `node:20-slim` and `nginx:alpine` resolved to when this was written.
FROM node:20.20.2-slim AS build

# No token is built into the page. A browser signs in at runtime (docs/sign-in.md), and the
# VITE_AUTH_TOKEN / VITE_CONFIG_TOKEN build arguments this image used to take are gone.

WORKDIR /app
COPY src/fleet-dashboard/package.json src/fleet-dashboard/package-lock.json ./
RUN npm ci
COPY src/fleet-dashboard/ .
RUN npm run build

FROM nginx:1.31.8-alpine
COPY --from=build /app/dist /usr/share/nginx/html
COPY src/fleet-dashboard/nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
