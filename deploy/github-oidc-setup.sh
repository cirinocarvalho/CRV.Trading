#!/usr/bin/env bash
# One-time bootstrap: set up GitHub Actions → Azure OIDC federation so every
# subsequent infra + deploy step runs inside GitHub Actions with no long-lived
# secrets. This is the only script you ever run locally.
#
# Requires:  az cli (logged in via `az login`), gh cli (logged in via `gh auth login`).
# Run from: repo root.

set -euo pipefail

# ── Config ───────────────────────────────────────────────────────────────────
RG="${CRV_RG:-crv-trading-rg}"
LOCATION="${CRV_LOCATION:-eastus}"
APP_REG_NAME="${CRV_APP_REG_NAME:-crv-trading-github-deploy}"
APP="${CRV_APP:-crv-trading}"
GITHUB_REPO="${GITHUB_REPO:-}"   # auto-detected via gh if empty

# ── Detect repo ──────────────────────────────────────────────────────────────
if [[ -z "$GITHUB_REPO" ]]; then
  GITHUB_REPO=$(gh repo view --json nameWithOwner -q .nameWithOwner 2>/dev/null || echo "")
fi
if [[ -z "$GITHUB_REPO" ]]; then
  echo "ERROR: not in a GitHub repo. Push to GitHub first (gh repo create ...) or set GITHUB_REPO=owner/repo." >&2
  exit 1
fi
echo "GitHub repo: $GITHUB_REPO"

SUB_ID=$(az account show --query id -o tsv)
TENANT_ID=$(az account show --query tenantId -o tsv)
echo "Subscription: $SUB_ID  Tenant: $TENANT_ID"

# ── 1. Resource group (so we can scope role assignments to it) ───────────────
az group create -n "$RG" -l "$LOCATION" -o none
RG_SCOPE=$(az group show -n "$RG" --query id -o tsv)

# ── 2. App registration + service principal ──────────────────────────────────
APP_ID=$(az ad app list --display-name "$APP_REG_NAME" --query '[0].appId' -o tsv)
if [[ -z "$APP_ID" ]]; then
  APP_ID=$(az ad app create --display-name "$APP_REG_NAME" --query appId -o tsv)
  echo "Created app registration: $APP_ID"
else
  echo "Reusing app registration: $APP_ID"
fi

SP_ID=$(az ad sp list --filter "appId eq '$APP_ID'" --query '[0].id' -o tsv)
if [[ -z "$SP_ID" ]]; then
  az ad sp create --id "$APP_ID" -o none
fi

# ── 3. Federated credentials for GitHub Actions ──────────────────────────────
add_fic() {
  local name="$1" subject="$2"
  if ! az ad app federated-credential list --id "$APP_ID" --query "[?name=='$name']" -o tsv | grep -q .; then
    az ad app federated-credential create --id "$APP_ID" --parameters "{
      \"name\": \"$name\",
      \"issuer\": \"https://token.actions.githubusercontent.com\",
      \"subject\": \"$subject\",
      \"audiences\": [\"api://AzureADTokenExchange\"]
    }" -o none
    echo "  + federated credential: $name"
  fi
}
add_fic "github-main"       "repo:$GITHUB_REPO:ref:refs/heads/main"
add_fic "github-master"     "repo:$GITHUB_REPO:ref:refs/heads/master"
add_fic "github-pr"         "repo:$GITHUB_REPO:pull_request"
add_fic "github-production" "repo:$GITHUB_REPO:environment:production"

# ── 4. Role assignment: Contributor on the RG (Bicep needs this) ────────────
# Scoped to the RG only — can't touch anything outside it.
az role assignment create \
  --assignee "$APP_ID" \
  --scope "$RG_SCOPE" \
  --role "Contributor" -o none 2>/dev/null || true

# User Access Administrator on the RG — Bicep creates role assignments
# (webapp → ACR pull, webapp → KV secrets user) which requires this.
az role assignment create \
  --assignee "$APP_ID" \
  --scope "$RG_SCOPE" \
  --role "User Access Administrator" -o none 2>/dev/null || true

# ── 5. Write GitHub repo secrets ─────────────────────────────────────────────
gh secret set AZURE_CLIENT_ID       --repo "$GITHUB_REPO" --body "$APP_ID"
gh secret set AZURE_TENANT_ID       --repo "$GITHUB_REPO" --body "$TENANT_ID"
gh secret set AZURE_SUBSCRIPTION_ID --repo "$GITHUB_REPO" --body "$SUB_ID"

cat <<EOF

─────────────────────────────────────────────────────────────────────
✓ Bootstrap complete.

Everything else now runs in GitHub Actions:
  • infra.yml   — provisions/updates Azure resources via Bicep
  • deploy.yml  — builds image + deploys to App Service
  • ci.yml      — build + test on every push/PR

Secrets written to $GITHUB_REPO:
  AZURE_CLIENT_ID        = $APP_ID
  AZURE_TENANT_ID        = $TENANT_ID
  AZURE_SUBSCRIPTION_ID  = $SUB_ID

Next:
  1. Commit & push (triggers infra.yml → provisions RG contents,
     then deploy.yml → builds + deploys first real image).
       git add -A && git commit -m "ci: add Azure Bicep + workflows" && git push

  2. Recommended: GitHub → Settings → Environments → New 'production'
     with required reviewers, so infra + deploy need approval.

  3. After first successful run, register OAuth redirect URIs with
     each broker — see deploy/OAUTH_REDIRECTS.md

Tail logs once deployed:
    az webapp log tail -g $RG -n $APP
─────────────────────────────────────────────────────────────────────
EOF
