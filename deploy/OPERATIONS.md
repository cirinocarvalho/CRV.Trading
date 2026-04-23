# Day-2 operations

Reference for running CRV.Trading in production on Azure. Covers logs,
database access, recovery, and common troubleshooting.

## Endpoints

| URL | Auth | Purpose |
|---|---|---|
| https://crv-trading.azurewebsites.net | Entra Easy Auth (user login) | App UI |
| https://crv-trading.azurewebsites.net/api/engine/status | None | Health check, JSON status |
| https://crv-trading.azurewebsites.net/api/engine/webhook/order | None | External order entry (TradingView) |
| https://crv-trading.scm.azurewebsites.net | Azure portal login | Kudu (logs, file system, web SSH) |
| https://crv-trading.scm.azurewebsites.net/webssh/host | Azure portal login | Shell in the SCM container |

The two auth-excluded paths above are whitelisted in `authExcludedPaths`
in `main.bicep`. Everything else requires a signed-in Entra user.

## Logs

```bash
# Stream all logs live
az webapp log tail -g crv-trading-rg -n crv-trading

# Download the current log bundle
az webapp log download -g crv-trading-rg -n crv-trading --log-file /tmp/logs.zip
unzip -o /tmp/logs.zip -d /tmp/logs/
```

Log file layout (under `/home/LogFiles/`):

| File | What's in it |
|---|---|
| `*_docker.log` | Azure hosting events (container pull / start / stop / probe) |
| `*_default_docker.log` | **App stdout/stderr** — Serilog output + ASP.NET request logs |
| `*_default_scm_docker.log` | Kudu SCM container logs |

## SQLite access

The live DB lives at `/home/data/crv_trading.db` inside the container. Two
ways to query it:

### Read-only query (no changes to the app)

```bash
# In the Kudu web SSH (https://crv-trading.scm.azurewebsites.net/webssh/host):
which sqlite3 || (apt-get update -qq && apt-get install -y -qq sqlite3)
sqlite3 /home/data/crv_trading.db "PRAGMA wal_checkpoint(FULL);"  # flush WAL
sqlite3 /home/data/crv_trading.db -header -column \
    "SELECT Id, Name, Broker, UpdatedAt FROM Configs;"
```

### Download a copy locally

```bash
PUB=$(az webapp deployment list-publishing-credentials -g crv-trading-rg -n crv-trading \
      --query "{u:publishingUserName,p:publishingPassword}" -o tsv)
USER=$(echo "$PUB" | cut -f1); PASS=$(echo "$PUB" | cut -f2)

for f in crv_trading.db crv_trading.db-wal crv_trading.db-shm; do
    curl -sS -u "$USER:$PASS" \
         "https://crv-trading.scm.azurewebsites.net/api/vfs/data/$f" \
         -o "/tmp/$f"
done

sqlite3 /tmp/crv_trading.db ".tables"
# Or open in DB Browser for SQLite / TablePlus / DataGrip
```

### Writing to the live DB

**Don't.** The app runs with an exclusive SQLite WAL; external writes from
the SCM shell can corrupt state. If you truly need to mutate data:

```bash
az webapp stop  -g crv-trading-rg -n crv-trading    # stop the app first
# ... run your UPDATE / DELETE ...
az webapp start -g crv-trading-rg -n crv-trading
```

## Litestream backup + restore

Litestream streams every SQLite WAL write to Azure Blob (container
`litestream`, snapshot every 6h, 24h of retention).

### Verify backups are flowing

```bash
STORAGE=$(az storage account list -g crv-trading-rg --query '[0].name' -o tsv)
az storage blob list --account-name "$STORAGE" --container-name litestream \
    --auth-mode login --query '[].{name:name, last:properties.lastModified, size:properties.contentLength}' \
    -o table | head
```

You should see `crv_trading/generations/...` objects with recent timestamps.

### Restore from backup

```bash
# Local copy of the latest backup
STORAGE=$(az storage account list -g crv-trading-rg --query '[0].name' -o tsv)
KEY=$(az storage account keys list -g crv-trading-rg --account-name "$STORAGE" \
      --query '[0].value' -o tsv)

docker run --rm -v "$PWD:/out" \
    -e LITESTREAM_AZURE_ACCOUNT_NAME="$STORAGE" \
    -e LITESTREAM_AZURE_ACCOUNT_KEY="$KEY" \
    benbjohnson/litestream:latest \
    restore -o /out/crv_trading.db \
    "azblob://${STORAGE}/litestream/crv_trading"

# Point-in-time restore
docker run --rm -v "$PWD:/out" \
    -e LITESTREAM_AZURE_ACCOUNT_NAME="$STORAGE" \
    -e LITESTREAM_AZURE_ACCOUNT_KEY="$KEY" \
    benbjohnson/litestream:latest \
    restore -timestamp 2026-04-23T15:30:00Z -o /out/crv_trading.db \
    "azblob://${STORAGE}/litestream/crv_trading"
```

Note: the upstream `benbjohnson/litestream` Docker image may not include
the `azblob` backend. If you see `"unknown replica type in config: azblob"`,
build from source the same way the app's Dockerfile does — or SSH into the
app container and use the binary already there at `/usr/local/bin/litestream`.

### Full disaster recovery (DB lost)

1. Stop the app: `az webapp stop -g crv-trading-rg -n crv-trading`
2. Remove the corrupted DB from `/home/data/crv_trading.db` via Kudu
3. Start the app: `az webapp start -g crv-trading-rg -n crv-trading`
4. The entrypoint runs `litestream restore -if-replica-exists -if-db-not-exists`
   and repopulates from blob before starting the app

## Secrets

See [SECRETS.md](SECRETS.md) for rotation, the placeholder-overwrite trap,
and the KV-reference cache recovery procedure.

## Timezone

The container runs in `America/New_York` (via `TZ` + `WEBSITE_TIME_ZONE`
env vars). All `DateTime.Now` values and log timestamps are in ET — matching
trading session time. `DateTime.UtcNow` remains UTC and is the canonical
storage timestamp.

## Force a refresh of KV references

If app settings show a literal `@Microsoft.KeyVault(...)` string in logs,
or the running app has stale secret values (e.g. `client_id=CHANGE_ME` in
OAuth URLs):

```bash
# Re-save the references to bust any cached resolution
KV=$(az keyvault list -g crv-trading-rg --query '[0].name' -o tsv)
az webapp config appsettings set -g crv-trading-rg -n crv-trading --settings \
  "Schwab__AppKey=@Microsoft.KeyVault(VaultName=$KV;SecretName=Schwab--AppKey)" \
  "Schwab__AppSecret=@Microsoft.KeyVault(VaultName=$KV;SecretName=Schwab--AppSecret)" \
  # ...repeat for every secret...
  -o none
# Set op auto-restarts the app. If it doesn't, restart manually:
az webapp restart -g crv-trading-rg -n crv-trading
```

## Engine lifecycle

| Action | Command |
|---|---|
| Start engine via UI | https://crv-trading.azurewebsites.net/settings/live → Start Engine |
| Stop engine via UI | Same page → Stop Engine |
| Restart container | `az webapp restart -g crv-trading-rg -n crv-trading` |
| Stop container | `az webapp stop -g crv-trading-rg -n crv-trading` |
| Start container | `az webapp start -g crv-trading-rg -n crv-trading` |
| Tail logs | `az webapp log tail -g crv-trading-rg -n crv-trading` |

## Common failure modes

### Health check "unhealthy"

Azure's health probe hits `healthCheckPath` (set to `/api/engine/status`
in Bicep) and expects HTTP 200. If the app is returning 401 there, Easy
Auth is gating the endpoint incorrectly — verify `/api/engine/status` is
in `authExcludedPaths`. If 5xx, check `*_default_docker.log` for startup
errors.

### Settings don't persist after save (appear to reset)

Check the app log for `Failed to save StrategyConfig to DB` — likely a
schema constraint. Previously resolved: `NOT NULL constraint failed:
Configs.EmailRecipients`. Fix is in `StrategyConfigService.Update()`
(coerces known-NOT-NULL columns to `""` before save) and a startup-time
repair pass in `Program.cs`.

### `invalid_client / Unauthorized` from Schwab OAuth

Almost always one of:
1. Prod redirect URI not registered in the Schwab developer portal app
2. `client_id=CHANGE_ME` in the authorize URL — KV secret reset to
   placeholder (see SECRETS.md — Bicep re-apply with
   `seedPlaceholderSecrets=true` clobbers real values)
3. Authorization code expired (Schwab codes live ~30 s) — retry

### App returns `401` to every request

Entra Easy Auth is working as intended. Log in at the root URL, or hit
an auth-excluded path (`/api/engine/status`, `/api/engine/webhook/order`).

### `invalid_grant` — "redirect_uri mismatch"

The URI sent at `/oauth/authorize` must match the one sent at `/oauth/token`
byte-for-byte. Verify `Schwab__RedirectUri` app setting is exactly the URI
registered in Schwab (including HTTP/HTTPS, trailing slash, case).

### Resource quotas

App Service B1 counts against "Basic VMs" vCPU quota, which on fresh
subscriptions is often 0. Either request a quota increase in the Azure
portal (Quotas → Compute) or switch SKU to `P0v3` / `S1` (different
quota bucket).

### Image pull failures on first deploy

App Service needs 1–2 min for the managed identity → ACR Pull role
assignment to propagate. If the first deploy fails with an ACR auth
error, retry: `gh workflow run deploy.yml`.

### .NET SDK resolution fails in CI

`global.json` pins to `version: 10.0.100` with `rollForward: major` —
accepts any 10.x+ SDK. If `dotnet restore` fails with "SDK not found",
check that the runner actually has any .NET 10 SDK installed. The
diagnostic step in `ci.yml` prints the resolved SDK version.

## Monitoring (future)

Not configured yet. When you're ready:

```bash
# Enable Application Insights on the Web App
az monitor app-insights component create \
    -g crv-trading-rg \
    -a crv-trading-insights \
    -l eastus \
    --application-type web

# Wire the instrumentation key into app settings
KEY=$(az monitor app-insights component show \
      -g crv-trading-rg -a crv-trading-insights \
      --query instrumentationKey -o tsv)
az webapp config appsettings set -g crv-trading-rg -n crv-trading --settings \
  APPINSIGHTS_INSTRUMENTATIONKEY=$KEY
```

## Related docs

- [DEPLOY.md](DEPLOY.md) — initial deployment runbook
- [SECRETS.md](SECRETS.md) — credential inventory and rotation
- [OAUTH_REDIRECTS.md](OAUTH_REDIRECTS.md) — broker app URI registration
