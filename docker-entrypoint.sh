#!/bin/sh
# Entrypoint: restore SQLite from Azure Blob on first boot (if no local DB but
# a replica exists), then replicate continuously while running the app.
#
# `litestream replicate -exec` runs the child process as PID 1's target:
#   - forwards SIGTERM for graceful shutdown
#   - replicates until the child exits, then flushes + exits with its code
#
# If LITESTREAM_AZURE_ACCOUNT_NAME is unset, skip Litestream entirely (lets you
# run the container locally without a backup target).

set -e

mkdir -p /home/data

if [ -z "${LITESTREAM_AZURE_ACCOUNT_NAME:-}" ]; then
    echo "[entrypoint] LITESTREAM_AZURE_ACCOUNT_NAME unset — running without backup"
    exec dotnet /app/CRV.Web.dll
fi

# Pre-flight: confirm litestream understands the config. If not (e.g. missing
# azblob backend in the binary), skip replication rather than killing the app.
if ! litestream databases -config /etc/litestream.yml > /dev/null 2>&1; then
    echo "[entrypoint] Litestream config invalid — skipping replication, running app directly"
    exec dotnet /app/CRV.Web.dll
fi

echo "[entrypoint] Restoring SQLite from Azure Blob if needed..."
litestream restore \
    -config /etc/litestream.yml \
    -if-replica-exists \
    -if-db-not-exists \
    /home/data/crv_trading.db || {
        echo "[entrypoint] Restore failed; continuing (DB may not exist yet)"
    }

echo "[entrypoint] Starting Litestream replication + app"
exec litestream replicate \
    -config /etc/litestream.yml \
    -exec "dotnet /app/CRV.Web.dll"
