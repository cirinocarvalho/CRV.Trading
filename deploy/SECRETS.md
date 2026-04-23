# Secrets inventory

Every credential in production lives in **Azure Key Vault**, referenced from
App Service app settings as `@Microsoft.KeyVault(...)`. The Web App's
system-assigned managed identity resolves them at startup.

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
| Entra Easy Auth client secret | 🔴 | KV secret `Auth--ClientSecret` |
| Storage key (Litestream) | 🔴 | KV secret `Litestream--StorageKey` (auto-populated from `listKeys()`) |

### Naming convention

Azure Key Vault secret names only allow `[A-Za-z0-9-]`, so `:` isn't valid.
Convention here is `Section--Key`, which maps back to `Section:Key` in .NET
config via the App Service reference. The App Service setting key uses `__`
(double underscore) as the section separator per standard .NET config
conventions.

| KV secret name | App Service setting name | .NET config key |
|---|---|---|
| `Schwab--AppKey` | `Schwab__AppKey` | `Schwab:AppKey` |

## Bootstrap (first-time setup on a fresh subscription)

After the **first** `infra.yml` run, the Key Vault exists but is empty —
`seedPlaceholderSecrets` defaults to `false` so Bicep doesn't touch secret
values. You have two options:

```bash
# Option A — seed placeholders via Bicep, then replace with real values
gh workflow run infra.yml -f seedPlaceholderSecrets=true
./deploy/set-secrets.sh     # replace all CHANGE_ME placeholders

# Option B — populate real values directly (set-secrets.sh creates-or-updates)
./deploy/set-secrets.sh

# Then, whichever path:
az webapp config appsettings set -g crv-trading-rg -n crv-trading --settings \
  Schwab__AccountId=... TradeStation__AccountId=... Tradovate__AccountId=...
az webapp restart -g crv-trading-rg -n crv-trading
```

> **Why this matters.** Earlier versions of the Bicep re-applied `CHANGE_ME`
> to every secret on every deploy (declarative resources always assert their
> state), silently wiping real values. The `seedPlaceholderSecrets` flag
> defaults to `false` so subsequent deploys leave your secrets alone.

## Rotation

```bash
./deploy/set-secrets.sh <family>         # schwab | tradestation | tradovate | smtp | all
az webapp restart -g crv-trading-rg -n crv-trading
```

App Service resolves the **latest** enabled version of a KV secret each time
the reference is resolved. A restart forces immediate re-resolution;
otherwise the cache refreshes within ~24h.

### ⚠️ The KV reference can get stuck on an old resolution

If the old version of a secret is still enabled and the App Service cache
hasn't seen the new one yet, the running container keeps using the old value
even after `set-secrets.sh`. Symptom: `client_id=CHANGE_ME` in OAuth URLs.

Recovery:

```bash
# Disable old versions so App Service MUST resolve the latest
KV=$(az keyvault list -g crv-trading-rg --query '[0].name' -o tsv)
for sec in Schwab--AppKey Schwab--AppSecret ...; do
  for v in $(az keyvault secret list-versions --vault-name "$KV" --name "$sec" \
             --query "[?attributes.enabled].id" -o tsv | head -n -1); do
    az keyvault secret set-attributes --id "$v" --enabled false -o none
  done
done
az webapp restart -g crv-trading-rg -n crv-trading
```

## Local development

Unchanged. `CRV.Web.csproj` has `<UserSecretsId>crv-trading-web</UserSecretsId>`,
so local secrets live in `~/.microsoft/usersecrets/crv-trading-web/secrets.json`:

```bash
cd CRV.Web
dotnet user-secrets set "Schwab:AppKey"             "..."
dotnet user-secrets set "Schwab:AppSecret"          "..."
dotnet user-secrets set "Schwab:AccountId"          "..."
dotnet user-secrets set "TradeStation:ClientId"     "..."
dotnet user-secrets set "TradeStation:ClientSecret" "..."
dotnet user-secrets set "TradeStation:AccountId"    "..."
dotnet user-secrets set "Tradovate:Username"        "..."
dotnet user-secrets set "Tradovate:Password"        "..."
dotnet user-secrets set "Tradovate:Cid"             "..."
dotnet user-secrets set "Tradovate:Secret"          "..."
dotnet user-secrets set "Tradovate:DeviceId"        "..."
dotnet user-secrets set "Tradovate:AccountId"       "..."
dotnet user-secrets set "Smtp:Password"             "..."
```

## Files

| File | Role |
|---|---|
| `deploy/main.bicep` | Declares KV + (conditionally) placeholder secrets; wires App Service settings to KV references |
| `deploy/set-secrets.sh` | Interactive populator / rotator |
| `deploy/app-entra.sh` | Sets up the Entra ID app registration used by Easy Auth |
| `CRV.Web/appsettings.json` | **No secrets or account IDs** — safe to commit |
| User-secrets / App Service / Key Vault | Real values per environment |

## Tier legend

- 🔴 **Tier 1 (must be in KV)** — credentials that authenticate to external APIs. Exposure = account takeover.
- 🟡 **Tier 2 (not in KV, but not in git)** — identifiers that are semi-sensitive (account numbers). Stored as plain App Service settings.
- ⚪ **Tier 3 (plain config)** — URLs, file paths, ports. Safe in git.

## Audit

Every KV secret read is logged in the vault's diagnostic settings. Enable
diagnostic logs on the KV resource and route to Log Analytics for an access
audit trail.

## What ships in the container image

Nothing secret. `.dockerignore` excludes `*_tokens.json`, `appsettings.Development.json`,
`.env`, and any `*.db`. The image contains only compiled binaries plus
`appsettings.json` (which has no secrets or account IDs).
