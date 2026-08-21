# Azure deployment — end-to-end

Deploying CRV.Trading to Azure App Service (Linux, container) with ACR,
Key Vault, Azure Blob (Litestream backup), and Entra ID Easy Auth.

## What gets deployed

| Resource | Purpose | Cost (~USD/mo) |
|---|---|---:|
| App Service plan (`crv-trading-plan`, Linux B1) | Compute — Always On, WebSockets, HTTPS-only | $13 |
| Web App (`crv-trading`, container) | Runs the ASP.NET Core image | — |
| ACR (`crvtrading<suffix>`, Basic) | Container registry | $5 |
| Key Vault (`crv-trading-kv-<suffix>`) | Broker + SMTP + storage secrets | $0.05 |
| Storage Account + `litestream` container | Continuous SQLite backup | $1 |
| Entra ID app registration | Easy Auth gate on the site | — |
| 2 role assignments | Web App MI → ACR Pull + KV Secrets User | — |
| **Total** | | **~$19** |


## Deploying under your own names

Nothing in this repo is pinned to one deployment. Every resource name comes
from a default that you can override without editing tracked files — useful
for a fork, a second environment, or a personal instance.

**GitHub Actions** reads repo variables (Settings → Secrets and variables →
Actions → Variables). Unset variables fall back to the defaults:

| Variable | Default | Controls |
|---|---|---|
| `AZURE_RG` | `crv-trading-rg` | Resource group for both workflows |
| `AZURE_APP` | `crv-trading` | Web App name, and the Bicep `appName` param that derives the plan, Key Vault, and ACR names |
| `AZURE_LOCATION` | `eastus` | Region |
| `SMTP_FROM_ADDRESS` | *(empty)* | Sender address for trade alerts. Empty = email alerts disabled |

**Local scripts** read environment variables with the same defaults:

```bash
CRV_RG=my-rg CRV_LOCATION=westus2 ./deploy/github-oidc-setup.sh
CRV_RG=my-rg ./deploy/set-secrets.sh
```

`CRV_APP_REG_NAME` overrides the Entra app-registration name in the bootstrap
script.

**Bicep** takes `appName` (default `crv-trading`); every other resource name is
derived from it plus a `uniqueString(resourceGroup().id)` suffix, so two
deployments in different resource groups never collide.

The commands and URLs throughout these runbooks use the defaults. If you
override the names, substitute accordingly — `https://<AZURE_APP>.azurewebsites.net`.

### Email sender

`Smtp:FromAddress` is intentionally empty in `appsettings.json` and defaults to
empty in Bicep, so the repo carries nobody's personal address. The app treats
unset SMTP as "not configured" and skips alerts with a logged warning rather
than failing. To enable email, set the `SMTP_FROM_ADDRESS` repo variable (or
the App Service `Smtp__FromAddress` setting) and store the password in Key
Vault as `Smtp--Password` via `./deploy/set-secrets.sh smtp`. `Smtp__Username`
defaults to the same address, which is what Gmail and most providers expect.
## Prerequisites

- Azure subscription with **App Service B1 vCPU quota** (check `az vm list-usage --location eastus`)
- `az` CLI ≥ 2.60 logged in (`az login`)
- `gh` CLI logged in (`gh auth login`)
- GitHub repo pushed (the OIDC setup federates credentials by repo name)

## Step 1 — One-time bootstrap (local)

This is the **only** local step in the whole pipeline. It creates the
resource group, app registration, federated credentials for GitHub Actions,
scoped role assignments, and writes three GitHub repo secrets.

```bash
./deploy/github-oidc-setup.sh
```

Sets: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` in the repo.

## Step 2 — First infra deploy

Resource providers need to be registered on fresh subscriptions:

```bash
for rp in Microsoft.ContainerRegistry Microsoft.Web Microsoft.KeyVault \
          Microsoft.Storage Microsoft.ManagedIdentity Microsoft.Authorization \
          Microsoft.Insights; do
    az provider register -n "$rp" -o none &
done; wait
```

Then trigger the first Bicep apply. On a brand-new env, either:

```bash
# Option A — seed placeholder secrets so app settings can resolve immediately
gh workflow run infra.yml -f seedPlaceholderSecrets=true

# Option B — skip seeding; populate directly via set-secrets.sh in Step 4
gh workflow run infra.yml
```

Watch:

```bash
gh run watch
```

~4 min. Creates ACR, Storage, KV (and optionally placeholder secrets), App Service
plan, Web App (with a stub container), role assignments.

## Step 3 — Set up Entra ID Easy Auth app registration

```bash
./deploy/app-entra.sh
```

This creates an Azure AD app registration that App Service's built-in
authentication layer uses to gate the site. Stores the client secret in
KV as `Auth--ClientSecret`.

If you want anonymous access (skip auth), leave `entraClientId` blank in
Bicep and remove the auth block from `main.bicep`.

## Step 4 — Populate real broker credentials

```bash
./deploy/set-secrets.sh
```

Prompts interactively, masked input, press enter to skip a secret.

Filters: `./deploy/set-secrets.sh schwab | tradestation | tradovate | smtp`

Then set the non-secret AccountIds:

```bash
az webapp config appsettings set -g crv-trading-rg -n crv-trading --settings \
  Schwab__AccountId=<schwab account id> \
  TradeStation__AccountId=SIM2841497F \
  Tradovate__AccountId=DEMO7063613
```

## Step 5 — First app deploy

```bash
gh workflow run deploy.yml
gh run watch
```

Runs CI (build + test), then builds the image in ACR with `crv-web:<sha>`
and `crv-web:latest` tags, updates the Web App to point at it, smoke-tests
`/api/engine/health`.

## Step 6 — Register broker OAuth redirect URIs

See [OAUTH_REDIRECTS.md](OAUTH_REDIRECTS.md). Add `https://crv-trading.azurewebsites.net/auth/<broker>`
alongside the dev `127.0.0.1:5001` URIs. Takes ~5 min to propagate.

## Step 7 — Verify

```bash
# Entra Easy Auth enforcing — expected
curl -s -o /dev/null -w "root:   HTTP %{http_code}\n" https://crv-trading.azurewebsites.net/

# Auth-excluded — liveness only, no trading data
curl -s https://crv-trading.azurewebsites.net/api/engine/health

# Broker OAuth flow
open https://crv-trading.azurewebsites.net/auth/schwab
```

Complete OAuth for each broker. Tokens land on `/home/data/*_tokens.json`
(persistent) and are replicated to blob by Litestream.

## Ongoing deploys

Any push to `master` triggers:

1. **CI** — build + test
2. **Deploy** — builds new image tagged `<commit sha>`, updates Web App, smoke-tests

Infra changes (when you edit `deploy/main.bicep`) trigger `infra.yml`:

1. **Plan** on PR — posts `what-if` diff as a PR comment
2. **Apply** on merge to master

## Rollback

Every image is tagged with its commit SHA. To revert:

```bash
gh workflow run deploy.yml -f image_tag=<earlier-commit-sha>
```

## Tear-down

```bash
# Delete everything in the RG (ACR, Storage, KV, plan, Web App)
az group delete -n crv-trading-rg --yes --no-wait

# Delete the Azure AD app registration (separate scope)
az ad app delete --id crv-trading-github-deploy
az ad app delete --id crv-trading   # Entra Easy Auth app (if created via app-entra.sh)

# Remove GitHub repo secrets
gh secret delete AZURE_CLIENT_ID
gh secret delete AZURE_TENANT_ID
gh secret delete AZURE_SUBSCRIPTION_ID
```

## Files that drive the deploy

| File | Role |
|---|---|
| `Dockerfile` | Multi-stage: builds .NET publish + Go-built Litestream + runtime image |
| `docker-entrypoint.sh` | Restores SQLite from Blob if needed, runs `litestream replicate -exec dotnet CRV.Web.dll` |
| `litestream.yml` | Replica config (6h snapshots, 24h retention, 1s WAL sync) |
| `global.json` | Pins .NET SDK to the 10.x stable channel |
| `deploy/main.bicep` | Declarative infra (all resources above) |
| `deploy/github-oidc-setup.sh` | One-time local bootstrap |
| `deploy/app-entra.sh` | Entra Easy Auth app registration |
| `deploy/set-secrets.sh` | Interactive secret populator / rotator |
| `.github/workflows/ci.yml` | Build + test |
| `.github/workflows/infra.yml` | Bicep deploy (what-if on PR, apply on master) |
| `.github/workflows/deploy.yml` | App container build + push + update + smoke test |

## Related docs

- [SECRETS.md](SECRETS.md) — credential inventory, rotation, tiers
- [OAUTH_REDIRECTS.md](OAUTH_REDIRECTS.md) — what to register where
- [OPERATIONS.md](OPERATIONS.md) — day-2 ops (DB access, logs, recovery)
