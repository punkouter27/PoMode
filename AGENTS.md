# AGENTS.md

Every rule for coding agents in this repository lives in [`CLAUDE.md`](CLAUDE.md) ("Working rules" and
NET_RULES), whichever agent you are. The architecture and the decisions already made are in [`docs/`](docs/).
Read both before changing anything. Keep this file a pointer, so the two can't drift apart again.

## CI/CD and Azure (same section in every Po repo)

- A push to `master` builds and deploys, then waits for `/health/live` to report the pushed commit (`.github/workflows/deploy.yml`). The workflow runs no tests, no infra job, no rollback and no PR runs: tests and `scripts/ci-checks.ps1` run locally before a push. The file is identical in every Po repo and each repo owns its copy. Nothing is deployed by hand.
- Azure sign-in is GitHub OIDC (the `gh-<repo>-deploy` app). Its IDs are repo **variables** `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`, and the App Service name is `AZURE_WEBAPP_NAME`. Never move them to secrets or write resource names into workflows.
- Every permission the app and CI need lives in `scripts/ensure-azure-access.ps1`. A role granted by hand and not added there is a bug: the next re-provision loses it.
- The app answers `/health/live` (process up, no dependencies, `X-Commit` header) and `/health` (dependencies reachable). The deploy waits for `/health/live` to carry the pushed commit.
- Tests pass on any machine: no drive-letter, temp-folder or working-directory assumptions, and no live config value hard-coded as an expected result (read it from the same config the app reads).
- A red run on the default branch gets an ntfy push from the CI watcher in PoPunkouterSoftware. Fix it, or say why it is red.
