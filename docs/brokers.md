# Broker Authentication

Schwab and TradeStation use **OAuth2 Authorization Code** flow with refresh tokens. Tradovate uses **direct credential POST** — no OAuth2 redirect.

The Live Settings page shows a connected/not-connected banner for each broker.

## Schwab

1. Create an app at [developer.schwab.com](https://developer.schwab.com) and register your redirect URI
2. Set `Schwab:AppKey` and `Schwab:AppSecret` in user-secrets
3. Set `Schwab:RedirectUri` in `appsettings.json` to match exactly
4. Navigate to `/auth/schwab` and click **Connect with Schwab**

| Token | Lifetime | On expiry |
|-------|----------|-----------|
| Access token | ~30 min | Auto-refreshed (60 s buffer) |
| Refresh token | 7 days of inactivity | Re-authenticate at `/auth/schwab` |

Credentials sent as **Basic auth header** (`Authorization: Basic base64(key:secret)`).

## TradeStation

1. Create an app at [developer.tradestation.com](https://developer.tradestation.com) and register redirect URI
2. Set `TradeStation:ClientId` and `TradeStation:ClientSecret` in user-secrets
3. Set `TradeStation:RedirectUri` in `appsettings.json` to match exactly
4. Navigate to `/auth/tradestation` and click **Connect with TradeStation**

Scopes: `openid profile offline_access MarketData ReadAccount Trade Crypto`

| Token | Lifetime | On expiry |
|-------|----------|-----------|
| Access token | ~20 min | Auto-refreshed (60 s buffer) |
| Refresh token | varies | Re-authenticate at `/auth/tradestation` |

Credentials sent as **form-body fields** (`client_id`/`client_secret`), not Basic auth.

## Tradovate

Direct credential POST — no OAuth2 browser redirect required.

1. Create a Tradovate API application (Settings > API) to obtain `cid` and `secret`
2. Set `Tradovate:Username`, `Tradovate:Password`, `Tradovate:Cid`, `Tradovate:Secret` in user-secrets
3. Configure `Tradovate:ApiBaseUrl`, `Tradovate:MdWssUrl`, `Tradovate:AccountId`, `Tradovate:TokenFile` in `appsettings.json`
4. Navigate to `/auth/tradovate` and click **Connect**

| Token | Lifetime | On expiry |
|-------|----------|-----------|
| Access token | 90 min | Auto-renewed when < 5 min remain |
| MD access token | 90 min | Renewed together with access token |

Tradovate returns two tokens: `accessToken` (REST) and `mdAccessToken` (WebSocket). For paper trading, use `https://demo.tradovateapi.com/v1` as `ApiBaseUrl`.

## Security

- Never commit credentials to source control — use `dotnet user-secrets` or environment variables
- Token files (`schwab_tokens.json`, `tradestation_tokens.json`, `tradovate_tokens.json`) are excluded by `.gitignore`
- `AccountId`, `RedirectUri`, and API URLs are non-sensitive and live in `appsettings.json`
