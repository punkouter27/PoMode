using 'main.bicep'

// What infra.yml applies. CI's identity is Contributor, which cannot write role assignments;
// grant those with scripts/ensure-azure-access.ps1 (or a local deploy with assignStorageRole=true).
param assignStorageRole = false
