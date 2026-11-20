@description('Location for the resources in this resource group')
param location string = resourceGroup().location

@description('Location for the Web App and App Service Plan')
param webAppLocation string = 'westus3'

@description('Name of the Storage Account')
param storageAccountName string = 'stpomode2'

@description('Name of the App Service (Web App)')
param webAppName string = 'app-pomode2'

@description('Name of the App Service Plan on F1 Free Linux tier')
param appServicePlanName string = 'asp-pomode-f1'

@description('Name of the shared Key Vault in PoShared')
param keyVaultName string = 'kv-poshared2'

@description('Name of the shared Application Insights component in PoShared')
param appInsightsName string = 'poappideinsights8f9c9a4e'

@description('Name of the shared resource group')
param sharedResourceGroupName string = 'PoShared'

@description('Deploy the optional Azure OpenAI interpreter. Off by default: it is the one resource that bills per use (roughly a cent per written interpretation).')
param enableCloudInterpreter bool = false

@description('Azure OpenAI account name (also its subdomain, so its endpoint is known before it exists)')
param openAiAccountName string = 'oai-pomode'

@description('Azure OpenAI model deployment name')
param openAiDeploymentName string = 'interpreter'

@description('Azure OpenAI location')
param openAiLocation string = 'eastus2'

// ─────────────────────────────────────────────
// Storage Account (stpomode2) in PoMode RG
// ─────────────────────────────────────────────

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
}

resource jobsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: 'pomode-jobs'
  properties: {
    publicAccess: 'None'
  }
}

// ─────────────────────────────────────────────
// Shared resources referenced from PoShared RG
// ─────────────────────────────────────────────

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
  scope: resourceGroup(sharedResourceGroupName)
}

resource sharedKeyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
  scope: resourceGroup(sharedResourceGroupName)
}

// ─────────────────────────────────────────────
// App Service Plan — F1 (Free), Linux, dedicated to PoMode
// ─────────────────────────────────────────────

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: webAppLocation
  sku: {
    name: 'F1'
    tier: 'Free'
  }
  properties: {
    reserved: true
  }
}

// ─────────────────────────────────────────────
// App Service (Web App) — .NET 10 on Linux with managed identity
// ─────────────────────────────────────────────

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: webAppLocation
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    enabled: true
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: 'dotnet PoMode.API.dll'
      alwaysOn: false
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: concat([
        {
          // Must match the key SecretsBootstrap reads, 'KeyVault:VaultUri'. Under the old name the
          // value never reached configuration, so the app silently used environment variables while
          // the access policy below granted secrets to an identity that never called the vault.
          name: 'KeyVault__VaultUri'
          value: sharedKeyVault.properties.vaultUri
        }
        {
          // Production: public guest cookie sessions, and the FakeAuth header scheme is not
          // registered outside Development/Test.
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'Jobs__Storage__AccountUri'
          value: storageAccount.properties.primaryEndpoints.blob
        }
        {
          name: 'Jobs__Storage__Container'
          value: 'pomode-jobs'
        }
        {
          name: 'Jobs__Storage__Mode'
          value: 'Auto'
        }
        {
          // The demo template runs the real pipeline on the server; on F1 that CPU comes out of the
          // 60-minute daily quota. New libraries start empty here instead.
          name: 'Demo__Enabled'
          value: 'false'
        }
        {
          name: 'ASPNETCORE_URLS'
          value: 'http://+:8080'
        }
      ], enableCloudInterpreter ? [
        {
          // An endpoint and a deployment name, never a key: the account has local auth disabled and
          // the app reaches it with its managed identity (AzureOpenAiSongInterpreter).
          name: 'PoMode__AzureOpenAI__Endpoint'
          value: 'https://${openAiAccountName}.openai.azure.com/'
        }
        {
          name: 'PoMode__AzureOpenAI__Deployment'
          value: openAiDeploymentName
        }
      ] : [])
    }
  }
}

// Roles and the Key Vault access policy for the app's identity: scripts/ensure-azure-access.ps1.

// The optional cloud interpreter.
module cloudInterpreter 'openai.bicep' = if (enableCloudInterpreter) {
  name: 'cloud-interpreter'
  params: {
    location: openAiLocation
    accountName: openAiAccountName
    deploymentName: openAiDeploymentName
  }
}

// Health ping availability test in PoShared
module healthAvailabilityTest 'availability-test.bicep' = {
  name: 'availability-test'
  scope: resourceGroup(sharedResourceGroupName)
  params: {
    appInsightsId: appInsights.id
    location: appInsights.location
    webAppHostName: webApp.properties.defaultHostName
    webAppName: webAppName
  }
}

// Outputs
output storageAccountName string = storageAccount.name
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output webAppPrincipalId string = webApp.identity.principalId
