# syntax=docker/dockerfile:1.7
# Multi-stage build for CRV.Web (ASP.NET Core + SignalR, .NET 10)

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Pin SDK via global.json (must be present before restore/build).
COPY global.json ./

# Copy solution + csprojs for better layer caching
COPY CRV.Trading.sln ./
COPY CRV.Core/CRV.Core.csproj             CRV.Core/
COPY CRV.Core.Tests/CRV.Core.Tests.csproj CRV.Core.Tests/
COPY CRV.Backtest/CRV.Backtest.csproj     CRV.Backtest/
COPY CRV.Live/CRV.Live.csproj             CRV.Live/
COPY CRV.Web/CRV.Web.csproj               CRV.Web/
RUN dotnet restore CRV.Web/CRV.Web.csproj

# Copy the rest and publish
COPY CRV.Core/      CRV.Core/
COPY CRV.Backtest/  CRV.Backtest/
COPY CRV.Live/      CRV.Live/
COPY CRV.Web/       CRV.Web/

RUN dotnet publish CRV.Web/CRV.Web.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ── Litestream (build from source, pre-built binaries lack azblob backend) ──
# LITESTREAM_BUILDER is overrideable so CI can point at an ACR-cached image and
# avoid Docker Hub's anonymous pull rate limit. Local `docker build` uses the
# default (Docker Hub).
ARG LITESTREAM_BUILDER=golang:1.25-alpine
FROM ${LITESTREAM_BUILDER} AS litestream
RUN apk add --no-cache git
WORKDIR /src
# Pin to a known-good commit. Update LITESTREAM_REF to track upstream.
ARG LITESTREAM_REF=main
RUN git clone --depth 1 --branch "${LITESTREAM_REF}" https://github.com/benbjohnson/litestream.git . \
 && CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/litestream ./cmd/litestream

# ── Runtime ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Litestream binary + config
COPY --from=litestream /out/litestream /usr/local/bin/litestream
COPY litestream.yml       /etc/litestream.yml
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

# App
COPY --from=build /app/publish ./

# App Service Linux expects the container to listen on $PORT (set via WEBSITES_PORT)
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DATA_DIR=/home/data

EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
