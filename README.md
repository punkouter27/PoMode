# PoMode

PoMode is a .NET 10 music-analysis application. Its API host serves a Blazor WebAssembly
client, accepts audio-analysis jobs, and coordinates background workers, browser-assisted
analysis, library and OneDrive integration, and optional song interpretation.

## Architecture

- `src/PoMode.Api/` hosts the client, Minimal API endpoints, SignalR analysis hub, and
  background analysis worker. Server features live in `Features/<Feature>/`.
- `src/PoMode.Client/` contains the Blazor WebAssembly UI.
- `src/PoMode.Shared/` contains contracts and source-generated JSON support shared by the
  API and client.
- `tests/` contains Unit, Integration, E2E API, and Playwright E2E UI projects.

The solution is `PoMode.slnx`. `CLAUDE.md` is the detailed guide to the architecture,
constraints, test budgets, and development workflow.

## Local development

Use the .NET SDK pinned by `global.json`. Start the shared Azurite emulator when needed,
then run the host:

```powershell
docker compose up -d
dotnet run --project src/PoMode.Api
```

The app serves on `http://localhost:5000` and `https://localhost:5001`. The Scalar API
reference is at `/scalar`; `/health`, `/health/live`, and `/health/ready` expose health
status.

## Verification

Run only the tests relevant to a change; Integration tests require Azurite and E2E UI
tests install and launch Playwright Chromium.

```powershell
dotnet build PoMode.slnx
dotnet test tests/PoMode.Unit --filter "FullyQualifiedName~TempoEstimator"
pwsh scripts/test-caps.ps1
```

See `CLAUDE.md` for targeted test commands, layout auditing, model setup, and the
project-specific verification limits.
