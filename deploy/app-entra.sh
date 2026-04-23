# Prereqs: az CLI logged in to the right subscription
#az login                                    # if not already
az account show --query name -o tsv         # verify the right subscription

# ── 1. Resolve your deployed resources ──────────────────────
RG="crv-trading-rg"
SITE=$(az webapp list -g "$RG" --query "[0].name" -o tsv)
HOST=$(az webapp show -g "$RG" -n "$SITE" --query defaultHostName -o tsv)
KV=$(az keyvault list -g "$RG" --query "[0].name" -o tsv)

echo "Site:    $SITE ($HOST)"
echo "KeyVault: $KV"

# ── 2. Create the Entra app registration ────────────────────
APP_ID=$(az ad app create \
  --display-name "crv-trading" \
  --sign-in-audience AzureADMyOrg \
  --web-redirect-uris "https://$HOST/.auth/login/aad/callback" \
  --enable-id-token-issuance true \
  --query appId -o tsv)

echo "Client ID (APP_ID): $APP_ID"

# Set the App ID URI so `api://<appId>` is a valid audience (matches the Bicep)
az ad app update --id "$APP_ID" --identifier-uris "api://$APP_ID"

# ── 3. Generate a client secret (2-year expiry) ─────────────
SECRET=$(az ad app credential reset \
  --id "$APP_ID" --years 2 \
  --display-name "easy-auth" \
  --query password -o tsv)

# ── 4. Store the secret in Key Vault ────────────────────────
az keyvault secret set \
  --vault-name "$KV" \
  --name "Auth--ClientSecret" \
  --value "$SECRET" \
  --output none

echo "✓ Auth--ClientSecret stored in $KV"

# ── 5. Create the Enterprise App (service principal) so you can restrict access
az ad sp create --id "$APP_ID" --query id -o tsv
SP_OBJECT_ID=$(az ad sp show --id "$APP_ID" --query id -o tsv)

# Require explicit user assignment (nobody in the tenant can sign in by default)
az ad sp update --id "$SP_OBJECT_ID" --set appRoleAssignmentRequired=true

# Assign yourself as the only allowed user
ME=$(az ad signed-in-user show --query id -o tsv)
az rest --method POST \
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$SP_OBJECT_ID/appRoleAssignedTo" \
  --body "{\"principalId\":\"$ME\",\"resourceId\":\"$SP_OBJECT_ID\",\"appRoleId\":\"00000000-0000-0000-0000-000000000000\"}" \
  --output none

echo ""
echo "════════════════════════════════════════════════════════"
echo "  Save this value for the Bicep redeploy:"
echo "    entraClientId = $APP_ID"
echo "════════════════════════════════════════════════════════"