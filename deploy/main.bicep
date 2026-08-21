// ════════════════════════════════════════════════════════════════════════════
// CRV.Trading — Azure infrastructure
// Scope: resource group. Create the RG and run via GitHub Actions (infra.yml).
//
// Secrets policy:
//   - Every credential lives in Key Vault. App Service reads them via
//     @Microsoft.KeyVault(...) references, resolved by the web app's
//     managed identity at startup.
//   - Broker/SMTP secrets are created here with placeholder value 'CHANGE_ME'.
//     Run deploy/set-secrets.sh after first apply to populate real values.
//   - Storage account key is populated automatically via listKeys() — no manual
//     step needed for Litestream.
// ════════════════════════════════════════════════════════════════════════════

targetScope = 'resourceGroup'

@description('Base name used for all resources. Must be globally unique for the web app hostname.')
param appName string = 'crv-trading'

@description('Azure region.')
param location string = resourceGroup().location

@description('App Service plan SKU. B1 = ~$13/mo Linux.')
param planSku string = 'B1'

@description('Container image to deploy. Defaults to a placeholder on first provision; deploy.yml flips this to the real image.')
param containerImage string = 'DOCKER|mcr.microsoft.com/appsvc/staticsite:latest'

@description('Placeholder value written into KV on first deploy. Not a real credential — real values are set by deploy/set-secrets.sh.')
param placeholderValue string = 'CHANGE_ME'

@description('Seed placeholder secrets in Key Vault. Set to true ONLY on first infra deploy to create the slots. On subsequent deploys leave false, otherwise Bicep will overwrite real secret values with the placeholder.')
param seedPlaceholderSecrets bool = false

@description('Seed App Service appSettings with the defaults defined in this template. Set to true ONLY on first deploy or when intentionally resetting config — otherwise manual Portal edits (AccountId, Tradovate URL for demo/live, SMTP overrides, etc.) will be wiped. Default: false, so routine infra deploys leave runtime config alone.')
param seedAppSettings bool = false

@description('Entra ID (Azure AD) app registration client ID for App Service Easy Auth. Leave blank to skip auth setup on first deploy. Create via: az ad app create --display-name crv-trading --web-redirect-uris https://<site>.azurewebsites.net/.auth/login/aad/callback')
param entraClientId string = ''

@description('SMTP sender address for trade alerts, e.g. alerts@example.com. Left empty by default: the app treats unset SMTP as "not configured" and skips email alerts rather than failing. Not a secret — the password lives in Key Vault as Smtp--Password.')
param smtpFromAddress string = ''

@description('SMTP account username. Defaults to smtpFromAddress, which is correct for Gmail and most providers.')
param smtpUsername string = ''

@description('Entra ID tenant ID. Defaults to the deployment subscription tenant. Override for multi-tenant scenarios (not recommended for this app).')
param entraTenantId string = subscription().tenantId

@description('Paths that bypass Easy Auth. Webhook endpoints and health checks — must remain reachable without a user login.')
param authExcludedPaths array = [
  // Anonymous liveness probe — carries no trading data. The full-state
  // '/api/engine/status' is deliberately NOT excluded: it returns P&L and
  // positions and must stay behind Easy Auth.
  '/api/engine/health'
  // External order entry (TradingView alerts). Cannot complete an interactive
  // login, so it is gated by the Webhook--Secret shared secret in the app
  // instead. That secret MUST be set, or the endpoint refuses every caller.
  '/api/engine/webhook/order'
]

@description('Tags applied to every resource.')
param tags object = {
  project: 'CRV.Trading'
  managedBy: 'bicep'
}

// Unique-but-stable suffix for globally-namespaced resources.
var suffix      = uniqueString(resourceGroup().id)
// Derived from appName so a second deployment does not inherit this one's
// names. For the default appName ('crv-trading') these evaluate byte-identically
// to the previous hardcoded literals — renaming them would orphan the existing
// Key Vault, ACR, and storage account.
var bareName    = toLower(replace(appName, '-', ''))
var acrName     = toLower('${bareName}${suffix}')
var kvName      = '${appName}-kv-${substring(suffix, 0, 6)}'
var storageName = toLower('${bareName}${substring(suffix, 0, 10)}')
var planName    = '${appName}-plan'
var siteName    = appName
var litestreamContainer = 'litestream'

// Built-in role IDs
var acrPullRoleId       = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var kvSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

// ── Azure Container Registry ────────────────────────────────────────────────
resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: acrName
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
  }
}

// ── Storage Account + Blob container (Litestream replica target) ────────────
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    accessTier: 'Hot'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 7 }
    containerDeleteRetentionPolicy: { enabled: true, days: 7 }
  }
}

resource litestreamBlobContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: litestreamContainer
  properties: {
    publicAccess: 'None'
  }
}

// ── Key Vault ───────────────────────────────────────────────────────────────
resource kv 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: kvName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enabledForTemplateDeployment: true
  }
}

// ── Secrets ─────────────────────────────────────────────────────────────────
// Litestream: populated directly from storage.listKeys(). No manual step.
resource secretLitestreamKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'Litestream--StorageKey'
  properties: {
    value: storage.listKeys().keys[0].value
    contentType: 'text/plain'
  }
}

// Broker + SMTP: placeholders. Real values set via deploy/set-secrets.sh.
// Note: Bicep won't overwrite an existing secret's value if we keep the same
// name — set-secrets.sh adds a NEW version with the real value, and the app
// setting reference (un-versioned) always resolves to the latest.
//
// Naming: Azure Key Vault secret names only allow [A-Za-z0-9-]. Use '--' as
// separator to round-trip back to ':' in .NET config via the reference.
var placeholderSecrets = [
  'Schwab--AppKey'
  'Schwab--AppSecret'
  'TradeStation--ClientId'
  'TradeStation--ClientSecret'
  'Tradovate--Username'
  'Tradovate--Password'
  'Tradovate--Cid'
  'Tradovate--Secret'
  'Tradovate--DeviceId'
  'Smtp--Password'
  'Auth--ClientSecret'
  'Webhook--Secret'
]

// Only create these on first run (seedPlaceholderSecrets=true). Once set-secrets.sh
// has populated real values, we MUST NOT overwrite them — so default is false and
// subsequent deploys leave the secrets alone.
resource secrets 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = [for name in placeholderSecrets: if (seedPlaceholderSecrets) {
  parent: kv
  name: name
  properties: {
    value: placeholderValue
    contentType: 'text/plain'
    attributes: { enabled: true }
  }
}]

// Helper: build an App Service @Microsoft.KeyVault(...) reference string.
func kvRef(vaultName string, secretName string) string =>
  '@Microsoft.KeyVault(VaultName=${vaultName};SecretName=${secretName})'

// ── App Service plan (Linux) ────────────────────────────────────────────────
resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: planSku }
  properties: {
    reserved: true
  }
}

// ── Web App (container) ─────────────────────────────────────────────────────
resource site 'Microsoft.Web/sites@2023-12-01' = {
  name: siteName
  location: location
  tags: tags
  kind: 'app,linux,container'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: containerImage
      acrUseManagedIdentityCreds: true
      alwaysOn: true
      webSocketsEnabled: true
      http20Enabled: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      // `/` 302-redirects to /dashboard, which Azure's health probe treats as unhealthy.
      // Point at an endpoint that returns 200 directly.
      healthCheckPath: '/api/engine/health'
      // NOTE: appSettings are NOT declared here. They live in the gated child
      // resource `siteAppSettings` below so routine Bicep redeploys don't wipe
      // manual Portal edits (AccountId, Tradovate demo/live URL, etc.).
    }
  }
}

// ── App Service appSettings (seeded on first deploy only) ──────────────────
// Gated behind `seedAppSettings` so subsequent infra deploys do NOT overwrite
// runtime config that's been edited in the Portal (demo vs live URLs, account
// IDs, SMTP overrides, etc.). To intentionally reset config to these defaults,
// pass `-p seedAppSettings=true` on the deploy.
resource siteAppSettings 'Microsoft.Web/sites/config@2023-12-01' = if (seedAppSettings) {
  parent: site
  name: 'appsettings'
  properties: {
    // ── Core runtime ────────────────────────────────────
    WEBSITES_PORT: '8080'
    WEBSITES_ENABLE_APP_SERVICE_STORAGE: 'true'
    ASPNETCORE_ENVIRONMENT: 'Production'
    DATA_DIR: '/home/data'
    // Run the container in US Eastern Time so DateTime.Now / ToLocalTime()
    // and all log timestamps match trading-session time (markets are ET).
    // WEBSITE_TIME_ZONE is App Service's documented knob; TZ is the POSIX
    // env var — setting both covers every library.
    WEBSITE_TIME_ZONE: 'Eastern Standard Time'
    TZ: 'America/New_York'
    ConnectionStrings__DefaultConnection: 'Data Source=/home/data/crv_trading.db'

    // ── Broker: Schwab (non-secret) ─────────────────────
    Schwab__ApiBaseUrl: 'https://api.schwabapi.com'
    Schwab__WssBaseUrl: 'wss://streamer-api.schwab.com/ws'
    Schwab__RedirectUri: 'https://${siteName}.azurewebsites.net/auth/schwab'
    Schwab__TokenFile: '/home/data/schwab_tokens.json'
    Schwab__AccountId: placeholderValue  // override in portal or set-secrets.sh
    // ── Broker: Schwab (secret → KV) ────────────────────
    Schwab__AppKey: kvRef(kv.name, 'Schwab--AppKey')
    Schwab__AppSecret: kvRef(kv.name, 'Schwab--AppSecret')

    // ── Broker: TradeStation (non-secret) ───────────────
    TradeStation__ApiBaseUrl: 'https://api.tradestation.com'
    TradeStation__AuthBaseUrl: 'https://signin.tradestation.com'
    TradeStation__RedirectUri: 'https://${siteName}.azurewebsites.net/auth/tradestation'
    TradeStation__TokenFile: '/home/data/tradestation_tokens.json'
    TradeStation__AccountId: placeholderValue
    // ── Broker: TradeStation (secret → KV) ──────────────
    TradeStation__ClientId: kvRef(kv.name, 'TradeStation--ClientId')
    TradeStation__ClientSecret: kvRef(kv.name, 'TradeStation--ClientSecret')

    // ── Broker: Tradovate (non-secret) ──────────────────
    Tradovate__ApiBaseUrl: 'https://live.tradovateapi.com/v1'
    Tradovate__MdWssUrl: 'wss://md.tradovateapi.com/v1/websocket'
    Tradovate__TokenFile: '/home/data/tradovate_tokens.json'
    Tradovate__AppId: 'CRVBot'
    Tradovate__AccountId: placeholderValue
    // ── Broker: Tradovate (secret → KV) ─────────────────
    Tradovate__Username: kvRef(kv.name, 'Tradovate--Username')
    Tradovate__Password: kvRef(kv.name, 'Tradovate--Password')
    Tradovate__Cid: kvRef(kv.name, 'Tradovate--Cid')
    Tradovate__Secret: kvRef(kv.name, 'Tradovate--Secret')
    Tradovate__DeviceId: kvRef(kv.name, 'Tradovate--DeviceId')

    // ── SMTP ─────────────────────────────────────────────
    Smtp__Host: 'smtp.gmail.com'
    Smtp__Port: '587'
    Smtp__UseSsl: 'true'
    Smtp__FromAddress: smtpFromAddress
    Smtp__Username: empty(smtpUsername) ? smtpFromAddress : smtpUsername
    Smtp__Password: kvRef(kv.name, 'Smtp--Password')

    // ── Litestream (backup to Azure Blob) ────────────────
    LITESTREAM_AZURE_ACCOUNT_NAME: storage.name
    LITESTREAM_AZURE_BUCKET: litestreamContainer
    LITESTREAM_AZURE_ACCOUNT_KEY: kvRef(kv.name, 'Litestream--StorageKey')

    // ── Order webhook shared secret ──────────────────────
    // Gates POST /api/engine/webhook/order, which is excluded from Easy Auth.
    // Until this holds a real value the endpoint refuses every caller (503).
    Webhook__Secret: kvRef(kv.name, 'Webhook--Secret')

    // ── Easy Auth (Microsoft Entra ID) client secret ─────
    // Resolved by Easy Auth at runtime via the setting name configured in
    // authsettingsV2.identityProviders.azureActiveDirectory.registration.
    MICROSOFT_PROVIDER_AUTHENTICATION_SECRET: kvRef(kv.name, 'Auth--ClientSecret')
  }
}

// ── App Service Easy Auth (Microsoft Entra ID) ─────────────────────────────
// Only configured once `entraClientId` is provided. Workflow:
//   1. First deploy with entraClientId='' — app is public, no auth.
//   2. Create the Entra app registration (one-time, manual):
//        az ad app create --display-name crv-trading \
//          --web-redirect-uris https://<siteHostname>/.auth/login/aad/callback \
//          --enable-id-token-issuance true
//      Then create a client secret and populate Auth--ClientSecret in KV.
//   3. Redeploy with entraClientId=<the app id>. Auth is now enforced.
resource siteAuth 'Microsoft.Web/sites/config@2023-12-01' = if (!empty(entraClientId)) {
  parent: site
  name: 'authsettingsV2'
  properties: {
    platform: {
      enabled: true
      runtimeVersion: '~1'
    }
    globalValidation: {
      requireAuthentication: true
      unauthenticatedClientAction: 'RedirectToLoginPage'
      redirectToProvider: 'azureactivedirectory'
      excludedPaths: authExcludedPaths
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          openIdIssuer: 'https://login.microsoftonline.com/${entraTenantId}/v2.0'
          clientId: entraClientId
          clientSecretSettingName: 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET'
        }
        validation: {
          allowedAudiences: [
            'api://${entraClientId}'
          ]
          defaultAuthorizationPolicy: {
            allowedPrincipals: {}
          }
        }
        login: {
          disableWWWAuthenticate: false
        }
      }
    }
    login: {
      tokenStore: {
        enabled: true
      }
      preserveUrlFragmentsForLogins: false
    }
    httpSettings: {
      requireHttps: true
      forwardProxy: {
        convention: 'NoProxy'
      }
    }
  }
}

// ── Role assignments ────────────────────────────────────────────────────────
// Web App MI → AcrPull on ACR
resource siteAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, site.id, acrPullRoleId)
  scope: acr
  properties: {
    principalType: 'ServicePrincipal'
    principalId: site.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
  }
}

// Web App MI → Key Vault Secrets User (needed to resolve @Microsoft.KeyVault refs)
resource siteKvRead 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(kv.id, site.id, kvSecretsUserRoleId)
  scope: kv
  properties: {
    principalType: 'ServicePrincipal'
    principalId: site.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
  }
}

// ── Outputs (consumed by GitHub Actions) ────────────────────────────────────
output acrName            string = acr.name
output acrLoginServer     string = acr.properties.loginServer
output siteName           string = site.name
output siteHostname       string = site.properties.defaultHostName
output keyVaultName       string = kv.name
output storageAccountName string = storage.name
output litestreamBucket   string = litestreamContainer
