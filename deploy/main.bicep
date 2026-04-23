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

@description('Tags applied to every resource.')
param tags object = {
  project: 'CRV.Trading'
  managedBy: 'bicep'
}

// Unique-but-stable suffix for globally-namespaced resources.
var suffix      = uniqueString(resourceGroup().id)
var acrName     = toLower('crvtrading${suffix}')
var kvName      = 'crv-trading-kv-${substring(suffix, 0, 6)}'
var storageName = toLower('crvtrading${substring(suffix, 0, 10)}')
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
]

resource secrets 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = [for name in placeholderSecrets: {
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
      healthCheckPath: '/api/engine/status'
      appSettings: [
        // ── Core runtime ────────────────────────────────────
        { name: 'WEBSITES_PORT',                          value: '8080' }
        { name: 'WEBSITES_ENABLE_APP_SERVICE_STORAGE',    value: 'true' }
        { name: 'ASPNETCORE_ENVIRONMENT',                 value: 'Production' }
        { name: 'DATA_DIR',                               value: '/home/data' }
        { name: 'ConnectionStrings__DefaultConnection',   value: 'Data Source=/home/data/crv_trading.db' }

        // ── Broker: Schwab (non-secret) ─────────────────────
        { name: 'Schwab__ApiBaseUrl',                     value: 'https://api.schwabapi.com' }
        { name: 'Schwab__WssBaseUrl',                     value: 'wss://streamer-api.schwab.com/ws' }
        { name: 'Schwab__RedirectUri',                    value: 'https://${siteName}.azurewebsites.net/auth/schwab' }
        { name: 'Schwab__TokenFile',                      value: '/home/data/schwab_tokens.json' }
        { name: 'Schwab__AccountId',                      value: placeholderValue }  // override in portal or set-secrets.sh
        // ── Broker: Schwab (secret → KV) ────────────────────
        { name: 'Schwab__AppKey',                         value: kvRef(kv.name, 'Schwab--AppKey') }
        { name: 'Schwab__AppSecret',                      value: kvRef(kv.name, 'Schwab--AppSecret') }

        // ── Broker: TradeStation (non-secret) ───────────────
        { name: 'TradeStation__ApiBaseUrl',               value: 'https://api.tradestation.com' }
        { name: 'TradeStation__AuthBaseUrl',              value: 'https://signin.tradestation.com' }
        { name: 'TradeStation__RedirectUri',              value: 'https://${siteName}.azurewebsites.net/auth/tradestation' }
        { name: 'TradeStation__TokenFile',                value: '/home/data/tradestation_tokens.json' }
        { name: 'TradeStation__AccountId',                value: placeholderValue }
        // ── Broker: TradeStation (secret → KV) ──────────────
        { name: 'TradeStation__ClientId',                 value: kvRef(kv.name, 'TradeStation--ClientId') }
        { name: 'TradeStation__ClientSecret',             value: kvRef(kv.name, 'TradeStation--ClientSecret') }

        // ── Broker: Tradovate (non-secret) ──────────────────
        { name: 'Tradovate__ApiBaseUrl',                  value: 'https://live.tradovateapi.com/v1' }
        { name: 'Tradovate__MdWssUrl',                    value: 'wss://md.tradovateapi.com/v1/websocket' }
        { name: 'Tradovate__TokenFile',                   value: '/home/data/tradovate_tokens.json' }
        { name: 'Tradovate__AppId',                       value: 'CRVBot' }
        { name: 'Tradovate__AccountId',                   value: placeholderValue }
        // ── Broker: Tradovate (secret → KV) ─────────────────
        { name: 'Tradovate__Username',                    value: kvRef(kv.name, 'Tradovate--Username') }
        { name: 'Tradovate__Password',                    value: kvRef(kv.name, 'Tradovate--Password') }
        { name: 'Tradovate__Cid',                         value: kvRef(kv.name, 'Tradovate--Cid') }
        { name: 'Tradovate__Secret',                      value: kvRef(kv.name, 'Tradovate--Secret') }
        { name: 'Tradovate__DeviceId',                    value: kvRef(kv.name, 'Tradovate--DeviceId') }

        // ── SMTP ─────────────────────────────────────────────
        { name: 'Smtp__Host',                             value: 'smtp.gmail.com' }
        { name: 'Smtp__Port',                             value: '587' }
        { name: 'Smtp__UseSsl',                           value: 'true' }
        { name: 'Smtp__FromAddress',                      value: 'cirino.carvalho@gmail.com' }
        { name: 'Smtp__Username',                         value: 'cirino.carvalho@gmail.com' }
        { name: 'Smtp__Password',                         value: kvRef(kv.name, 'Smtp--Password') }

        // ── Litestream (backup to Azure Blob) ────────────────
        { name: 'LITESTREAM_AZURE_ACCOUNT_NAME',          value: storage.name }
        { name: 'LITESTREAM_AZURE_BUCKET',                value: litestreamContainer }
        { name: 'LITESTREAM_AZURE_ACCOUNT_KEY',           value: kvRef(kv.name, 'Litestream--StorageKey') }
      ]
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
