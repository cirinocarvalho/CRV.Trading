# Secrets inventory

Every credential in production lives in **Azure Key Vault**, referenced from
App Service app settings as `@Microsoft.KeyVault(...)`. The Web App's system-
assigned managed identity resolves them at startup.

## Where each secret lives

| Config key (.NET) | Tier | Storage |
|---|---|---|
| `Schwab:AppKey` | 🔴 | KV secret `Schwab--AppKey` |
| `Schwab:AppSecret` | 🔴 | KV secret `Schwab--AppSecret` |
| `Schwab:AccountId` | 🟡 | App Service setting `Schwab__AccountId` |
| `TradeStation:ClientId` | 🔴 | KV secret `TradeStation--ClientId` |
| `TradeStation:ClientSecret` | 🔴 | KV secret `TradeStation--ClientSecret` |
| `TradeStation:AccountId` | 🟡 | App Service setting `TradeStation__AccountId` |
| `Tradovate:Username` | 🔴 | KV secret `Tradovate--Username` |
| `Tradovate:Password` | 🔴 | KV secret `Tradovate--Password` |
| `Tradovate:Cid` | 🔴 | KV secret `Tradovate--Cid` |
| `Tradovate:Secret` | 🔴 | KV secret `Tradovate--Secret` |
| `Tradovate:DeviceId` | 🔴 | KV secret `Tradovate--DeviceId` |
| `Tradovate:AccountId` | 🟡 | App Service setting `Tradovate__AccountId` |
| `Smtp:Password` | 🔴 | KV secret `Smtp--Password` |
| `LITESTREAM_AZURE_ACCOUNT_KEY` | 🔴 | KV secret `Litestream--StorageKey` (auto-populated) |

### Naming convention

Azure Key Vault secret names only allow `[A-Za-z0-9-]`, so `:` isn't valid.
The convention here is `Section--Key`, which maps back to `Section:Key` in
.NET config via the App Service reference. The App Service setting key uses
`__` (double underscore) as the section separator per standard .NET config
conventions.

Example:

| KV secret name | App Service setting name | .NET config key |
|---|---|---|
| `Schwab--AppKey` | `Schwab__AppKey` | `Schwab:AppKey` |

## Bootstrap (first-time setup)

After `infra.yml` finishes its first successful run, **every secret exists in
KV with a placeholder value `CHANGE_ME`**. The app will start but broker auth
will fail until you populate real values.

```bash
# Populate all secrets interactively:
./deploy/set-secrets.sh

# Or rotate one family:
./deploy/set-secrets.sh schwab
./deploy/set-secrets.sh tradestation
./deploy/set-secrets.sh tradovate
./deploy/set-secrets.sh smtp

# AccountIds are NOT secret — set directly:
az webapp config appsettings set -g crv-trading-rg -n crv-trading --settings \
  Schwab__AccountId=69E504138008BA79319C6752AD0D0157CBDE897C66E91072C7ADB6A1EC2A189B \
  TradeStation__AccountId=SIM2841497F \
  Tradovate__AccountId=DEMO7063613

# Then restart so KV references re-resolve:
az webapp restart -g crv-trading-rg -n crv-trading
```

## Rotation

To rotate any credential:

1. Update the secret at the broker's portal
2. `./deploy/set-secrets.sh <family>` — adds a new secret version in KV
3. `az webapp restart -g crv-trading-rg -n crv-trading`

App Service resolves the **latest** version each time the reference is
resolved (no SecretVersion pinning in the reference string). Restart forces
immediate re-resolution; otherwise it refreshes within ~24h.

## Local development

Nothing changes. `CRV.Web.csproj` has `<UserSecretsId>crv-trading-web</UserSecretsId>`,
so local secrets live in `~/.microsoft/usersecrets/crv-trading-web/secrets.json`.

```bash
# One-time per workstation:
dotnet user-secrets set "Schwab:AppKey"         "..." --project CRV.Web
dotnet user-secrets set "Schwab:AppSecret"      "..." --project CRV.Web
dotnet user-secrets set "Schwab:AccountId"      "..." --project CRV.Web
dotnet user-secrets set "TradeStation:ClientId"     "..." --project CRV.Web
dotnet user-secrets set "TradeStation:ClientSecret" "..." --project CRV.Web
dotnet user-secrets set "TradeStation:AccountId"    "..." --project CRV.Web
dotnet user-secrets set "Tradovate:Username"    "..." --project CRV.Web
dotnet user-secrets set "Tradovate:Password"    "..." --project CRV.Web
dotnet user-secrets set "Tradovate:Cid"         "..." --project CRV.Web
dotnet user-secrets set "Tradovate:Secret"      "..." --project CRV.Web
dotnet user-secrets set "Tradovate:DeviceId"    "..." --project CRV.Web
dotnet user-secrets set "Tradovate:AccountId"   "..." --project CRV.Web
dotnet user-secrets set "Smtp:Password"         "..." --project CRV.Web
```

## Files this touches

- `deploy/main.bicep` — declares KV, KV secrets (placeholder values), wires
  App Service settings to KV references
- `deploy/set-secrets.sh` — interactive populator / rotator
- `CRV.Web/appsettings.json` — **no secrets or account IDs**, safe to commit
- User-secrets / App Service settings / Key Vault — real values by environment

## Audit

Every read of a KV secret is logged in the vault's diagnostic settings. Enable
diagnostic logs on the KV resource and route to Log Analytics / Storage for
access audit trails.

## Tier legend

- 🔴 **Tier 1 (must be in KV)** — credentials that authenticate to external
  APIs. Exposure = account takeover.
- 🟡 **Tier 2 (not in KV, but not in git either)** — identifiers that are
  semi-sensitive (account numbers). Exposure = information disclosure, not
  compromise. Stored as plain App Service settings.
- ⚪ **Tier 3 (plain config)** — URLs, file paths, ports. Safe in git.

## What ships in the container image

Nothing secret. `.dockerignore` excludes:

- `*_tokens.json` (runtime OAuth tokens)
- `appsettings.Development.json`
- `.env`, `crv_trading.db`

The image contains only compiled binaries + `appsettings.json` (which now
has no secrets or account IDs).
