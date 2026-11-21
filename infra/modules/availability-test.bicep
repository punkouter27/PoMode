// Availability test (a standard test: Microsoft retired the classic URL ping tests) for /health/live.
// Must live in the same resource group and location as the App Insights component (PoShared/eastus2).

@description('Resource ID of the App Insights component to link the test to')
param appInsightsId string

@description('Location — MUST match the linked App Insights component location')
param location string

@description('Public host name of the web app to ping (no scheme)')
param webAppHostName string

@description('Web app name, used to name the test')
param webAppName string

resource healthAvailabilityTest 'Microsoft.Insights/webtests@2022-06-15' = {
  name: 'avail-${webAppName}-health'
  location: location
  tags: {
    'hidden-link:${appInsightsId}': 'Resource'
  }
  kind: 'standard'
  properties: {
    SyntheticMonitorId: 'avail-${webAppName}-health'
    Name: '${webAppName}-health'
    Description: 'Availability test for ${webAppName} /health/live'
    Enabled: true
    // One location every 15 minutes, against the dependency-free probe. The F1 site gets 60 CPU
    // minutes a day; three locations every 5 minutes on the deep /health meant ~860 wake-ups a day.
    Frequency: 900
    Timeout: 30
    Kind: 'standard'
    RetryEnabled: true
    Locations: [
      {
        Id: 'us-ca-sjc-azr' // West US
      }
    ]
    Request: {
      RequestUrl: 'https://${webAppHostName}/health/live'
      HttpVerb: 'GET'
      ParseDependentRequests: false
    }
    ValidationRules: {
      ExpectedHttpStatusCode: 200
      SSLCheck: false
    }
  }
}
