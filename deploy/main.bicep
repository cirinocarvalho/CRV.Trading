// ════════════════════════════════════════════════════════════════════════════
// CRV.Trading — Azure infrastructure
// Scope: resource group. Create the RG and run via GitHub Actions (infra.yml).
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

@description('Tags applied to every resource.')
param tags object = {
  project: 'CRV.Trading'
  managedBy: 'bicep'
}

// Unique-but-stable suffix for globally-namespaced resources (ACR, KV).
var suffix    = uniqueString(resourceGroup().id)
var acrName   = toLower('crvtrading${suffix}')
var kvName    = 'crv-trading-kv-${substring(suffix, 0, 6)}'
var planName  = '${appName}-plan'
var siteName  = appName

// ── Azure Container Registry ────────────────────────────────────────────────
resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: acrName
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false   // use managed identity instead
  }
}

// ── App Service plan (Linux) ────────────────────────────────────────────────
resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: planSku }
  properties: {
    reserved: true  // required for Linux
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
      healthCheckPath: '/'
      appSettings: [
        { name: 'WEBSITES_PORT',                          value: '8080' }
        { name: 'WEBSITES_ENABLE_APP_SERVICE_STORAGE',    value: 'true' }
        { name: 'ASPNETCORE_ENVIRONMENT',                 value: 'Production' }
        { name: 'DATA_DIR',                               value: '/home/data' }
        { name: 'ConnectionStrings__DefaultConnection',   value: 'Data Source=/home/data/crv_trading.db' }
        { name: 'Schwab__TokenFile',                      value: '/home/data/schwab_tokens.json' }
        { name: 'Schwab__RedirectUri',                    value: 'https://${siteName}.azurewebsites.net/auth/schwab' }
        { name: 'TradeStation__TokenFile',                value: '/home/data/tradestation_tokens.json' }
        { name: 'TradeStation__RedirectUri',              value: 'https://${siteName}.azurewebsites.net/auth/tradestation' }
        { name: 'Tradovate__TokenFile',                   value: '/home/data/tradovate_tokens.json' }
      ]
    }
  }
}

// ── Key Vault (broker secrets) ──────────────────────────────────────────────
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
  }
}

// ── Role assignments ────────────────────────────────────────────────────────
// Web App MI → AcrPull on ACR
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
resource siteAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, site.id, acrPullRoleId)
  scope: acr
  properties: {
    principalType: 'ServicePrincipal'
    principalId: site.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
  }
}

// Web App MI → Key Vault Secrets User
var kvSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
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
output acrName        string = acr.name
output acrLoginServer string = acr.properties.loginServer
output siteName       string = site.name
output siteHostname   string = site.properties.defaultHostName
output keyVaultName   string = kv.name
