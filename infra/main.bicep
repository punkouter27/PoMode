targetScope = 'subscription'

@minLength(1)
@description('Primary location for resource group and storage')
param location string = 'eastus2'

@minLength(1)
@description('Location for Web App and App Service Plan')
param webAppLocation string = 'westus3'

// Resources only. Every role and access policy the app needs is granted by
// scripts/ensure-azure-access.ps1 (AGENTS.md), never here, so there is one list to keep right.

@description('Deploy the optional Azure OpenAI interpreter (bills per use, a fraction of a cent per written interpretation).')
param enableCloudInterpreter bool = false

var resourceGroupName = 'PoMode'
var sharedResourceGroupName = 'PoShared'
var storageAccountName = 'stpomode2'
var webAppName = 'app-pomode2'
var appServicePlanName = 'asp-pomode-f1'
var keyVaultName = 'kv-poshared2'
var appInsightsName = 'poappideinsights8f9c9a4e'

// Target Resource Group
resource poModeRg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

// Deploy PoMode application infrastructure into the PoMode RG
module resources 'modules/resources.bicep' = {
  name: 'pomode-resources'
  scope: poModeRg
  params: {
    location: location
    webAppLocation: webAppLocation
    storageAccountName: storageAccountName
    webAppName: webAppName
    appServicePlanName: appServicePlanName
    keyVaultName: keyVaultName
    appInsightsName: appInsightsName
    sharedResourceGroupName: sharedResourceGroupName
    enableCloudInterpreter: enableCloudInterpreter
  }
}

output webAppUrl string = resources.outputs.webAppUrl
