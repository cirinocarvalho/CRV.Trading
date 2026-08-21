#!/usr/bin/env bash
# Interactive: populate real secret values in Azure Key Vault.
# Run after the first successful infra.yml deployment.
# Re-run any time to rotate a subset of secrets (empty input = leave unchanged).
#
# Requires: az cli logged in, with access to the Key Vault (RG Contributor or
# Key Vault Secrets Officer role).
#
# Usage:
#   ./deploy/set-secrets.sh            # prompts for every secret
#   ./deploy/set-secrets.sh schwab     # only prompts for Schwab-related secrets
#   ./deploy/set-secrets.sh smtp       # etc.
#   ./deploy/set-secrets.sh webhook    # order-webhook shared secret

set -euo pipefail

RG="${CRV_RG:-crv-trading-rg}"
APP="${CRV_APP:-crv-trading}"
FILTER="${1:-all}"

# Discover the Key Vault name (unique-suffix from Bicep)
KV=$(az keyvault list -g "$RG" --query '[0].name' -o tsv 2>/dev/null || echo "")
if [[ -z "$KV" ]]; then
  echo "ERROR: no Key Vault found in $RG. Deploy infra first via GitHub Actions (infra.yml)." >&2
  exit 1
fi
echo "Using Key Vault: $KV"
echo

# Helper: prompt for a secret, set it only if user enters non-empty text.
# Silent input, masked. Empty input = keep current value.
prompt_and_set() {
  local name="$1" label="$2"
  local value=""
  printf "  %s (%s)\n  → [enter to keep current]: " "$label" "$name"
  # shellcheck disable=SC2162
  read -s value
  echo
  if [[ -n "$value" ]]; then
    az keyvault secret set --vault-name "$KV" --name "$name" --value "$value" -o none
    echo "    ✓ updated $name"
  else
    echo "    · kept existing $name"
  fi
  echo
}

match() { [[ "$FILTER" == "all" || "$FILTER" == "$1" ]]; }

# ── Schwab ──────────────────────────────────────────────────────────────────
if match schwab; then
  echo "── Schwab (https://developer.schwab.com) ─────────────────────"
  prompt_and_set "Schwab--AppKey"    "App key"
  prompt_and_set "Schwab--AppSecret" "App secret"
fi

# ── TradeStation ────────────────────────────────────────────────────────────
if match tradestation || match ts; then
  echo "── TradeStation (https://developer.tradestation.com) ─────────"
  prompt_and_set "TradeStation--ClientId"     "Client ID"
  prompt_and_set "TradeStation--ClientSecret" "Client secret"
fi

# ── Tradovate ───────────────────────────────────────────────────────────────
if match tradovate || match tv; then
  echo "── Tradovate ─────────────────────────────────────────────────"
  prompt_and_set "Tradovate--Username" "Username"
  prompt_and_set "Tradovate--Password" "Password"
  prompt_and_set "Tradovate--Cid"      "CID (numeric)"
  prompt_and_set "Tradovate--Secret"   "App secret"
  prompt_and_set "Tradovate--DeviceId" "Device ID"
fi

# ── SMTP ────────────────────────────────────────────────────────────────────
if match smtp; then
  echo "── SMTP (for email alerts) ───────────────────────────────────"
  prompt_and_set "Smtp--Password" "SMTP password / app password"
fi

# ── Order webhook ───────────────────────────────────────────────────────────
# Gates POST /api/engine/webhook/order, which is excluded from Easy Auth so
# TradingView can reach it. Until this is set the endpoint refuses every
# caller. Generate a strong value with:  openssl rand -base64 32
if match webhook; then
  echo "── Order webhook (shared secret) ─────────────────────────────"
  prompt_and_set "Webhook--Secret" "Webhook shared secret (openssl rand -base64 32)"
fi

# ── Account IDs (not secrets — stored as plain App Service settings) ────────
if match accounts; then
  echo "── AccountIds (not KV — App Service settings) ────────────────"
  echo "  These aren't secrets; set them directly as App Service settings:"
  echo
  echo "    az webapp config appsettings set -g $RG -n $APP --settings \\"
  echo "      Schwab__AccountId=... \\"
  echo "      TradeStation__AccountId=... \\"
  echo "      Tradovate__AccountId=..."
  echo
fi

echo
echo "─────────────────────────────────────────────────────────────────────"
echo "✓ Done. Restart the web app for new values to take effect:"
echo "    az webapp restart -g $RG -n $APP"
echo "─────────────────────────────────────────────────────────────────────"
