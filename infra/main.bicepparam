using 'main.bicep'

// Applied by hand (AGENTS.md: az deployment sub create -l eastus2 -f infra/main.bicep -p infra/main.bicepparam).
// Roles are not here: scripts/ensure-azure-access.ps1 grants every one.
param enableCloudInterpreter = false
