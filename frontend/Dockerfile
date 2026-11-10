# syntax=docker/dockerfile:1
# Build output is plain JS, so the build stage runs natively on the CI machine for every target platform.
FROM --platform=$BUILDPLATFORM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM node:22-alpine AS runtime
WORKDIR /app
ENV NODE_ENV=production PORT=4000 HN_API_URL=http://api:8080
COPY --from=build /app/dist ./dist
USER node
EXPOSE 4000
CMD ["node", "dist/handynet-web/server/server.mjs"]
