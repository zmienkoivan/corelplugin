FROM node:24-bookworm-slim AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM node:24-bookworm-slim
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --omit=dev
COPY --from=build /app/dist ./dist
COPY server ./server
ENV PREVIEW_HOST=0.0.0.0 PREVIEW_PORT=4173 PREVIEW_DATA_DIR=/app/data
EXPOSE 4173
CMD ["node", "server/index.mjs"]
