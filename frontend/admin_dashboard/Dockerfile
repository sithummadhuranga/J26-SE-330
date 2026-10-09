# Admin dashboard served by nginx; build with: docker build -t melanin-wound-cdss/admin-dashboard frontend/admin_dashboard

FROM node:22-alpine AS build
# The browser calls the API gateway directly, so this is the gateway as the browser sees it, not a Docker hostname.
ARG API_BASE_URL=http://localhost:8080
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN VITE_API_BASE_URL=${API_BASE_URL} npm run build

FROM nginx:1.27-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist /usr/share/nginx/html
EXPOSE 80
