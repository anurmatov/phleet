FROM node:20-slim AS build

ARG VITE_AUTH_TOKEN=""
ENV VITE_AUTH_TOKEN=${VITE_AUTH_TOKEN}

WORKDIR /app
COPY src/fleet-dashboard/package.json .
RUN npm install
COPY src/fleet-dashboard/ .
RUN npm run build

FROM nginx:alpine
COPY --from=build /app/dist /usr/share/nginx/html
COPY src/fleet-dashboard/nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
