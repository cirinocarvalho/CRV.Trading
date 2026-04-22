# OAuth Redirect URIs

Production host: `https://crv-trading.azurewebsites.net`

Register **exactly** these URIs with each broker's developer portal. They must be
HTTPS and match character-for-character — trailing slashes matter.

## Schwab

- **Developer portal:** https://developer.schwab.com
- **Redirect URI:** `https://crv-trading.azurewebsites.net/auth/schwab`
- Corresponding App Service setting: `Schwab__RedirectUri`
- Secrets to store in Key Vault: `Schwab--ClientId`, `Schwab--ClientSecret`

## TradeStation

- **Developer portal:** https://developer.tradestation.com
- **Redirect URI:** `https://crv-trading.azurewebsites.net/auth/tradestation`
- Corresponding App Service setting: `TradeStation__RedirectUri`
- Secrets to store in Key Vault: `TradeStation--ClientId`, `TradeStation--ClientSecret`

## Tradovate

Tradovate uses direct-credential auth (no redirect URI), but you still need the
API credentials stored as secrets:

- Secrets: `Tradovate--Username`, `Tradovate--Password`, plus `AppId` / `AppVersion` / `cid` / `sec` as required by your integration.

## After registering

1. Deploy (`./deploy/azure-setup.sh`) — this sets the `*__RedirectUri` app settings.
2. Visit `https://crv-trading.azurewebsites.net/auth/schwab` and complete the OAuth flow.
   Token JSON will land in `/home/data/schwab_tokens.json` (persistent).
3. Repeat for TradeStation.
4. For Tradovate, use `/auth/tradovate` to submit username/password.

## Local dev

Keep the `127.0.0.1:5001` redirects in `appsettings.Development.json` — the Azure
settings override only in Production via `ASPNETCORE_ENVIRONMENT=Production`.
