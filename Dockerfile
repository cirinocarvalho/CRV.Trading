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

# ── Litestream (download binary) ─────────────────────────
FROM alpine:3.20 AS litestream
ARG LITESTREAM_VERSION=0.3.13
RUN apk add --no-cache curl ca-certificates \
 && curl -fsSL "https://github.com/benbjohnson/litestream/releases/download/v${LITESTREAM_VERSION}/litestream-v${LITESTREAM_VERSION}-linux-amd64.tar.gz" \
      | tar -xz -C /usr/local/bin litestream

# ── Runtime ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Litestream binary + config
COPY --from=litestream /usr/local/bin/litestream /usr/local/bin/litestream
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
