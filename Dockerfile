# syntax=docker/dockerfile:1.7
# Multi-stage build for CRV.Web (ASP.NET Core + SignalR, .NET 10)

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution + csprojs first for better layer caching
COPY CRV.Trading.sln ./
COPY CRV.Core/CRV.Core.csproj        CRV.Core/
COPY CRV.Core.Tests/CRV.Core.Tests.csproj CRV.Core.Tests/
COPY CRV.Backtest/CRV.Backtest.csproj CRV.Backtest/
COPY CRV.Live/CRV.Live.csproj        CRV.Live/
COPY CRV.Web/CRV.Web.csproj          CRV.Web/
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

# ── Runtime ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# App Service Linux expects the container to listen on $PORT (set via WEBSITES_PORT)
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DATA_DIR=/home/data

EXPOSE 8080
ENTRYPOINT ["dotnet", "CRV.Web.dll"]
