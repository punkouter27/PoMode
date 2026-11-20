using 'main.bicep'

// What a manual `az deployment` of main.bicep applies (there is no infra workflow). CI's identity is Contributor, which cannot write role assignments;
// grant those with scripts/ensure-azure-access.ps1 (or a local deploy with assignStorageRole=true).
param assignStorageRole = false
