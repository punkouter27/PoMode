# AGENTS.md — PoMode

Read this before changing anything; CLAUDE.md only points here. Sections 1-3 are the rules every session follows;
"This repo" below them is what is specific to PoMode, including where it deviates from the standard and why.

## 1. Standard

> This repo owns every file in it. Nothing here is generated from, synced with, or required to stay identical to
> another repository. Headers like "SAME FILE IN EVERY PO …", "Generated from po-shared … DO NOT EDIT", and scripts
> that copy files into or check other repos are forbidden. Only Azure resources in the `PoShared` resource group
> may be shared.

The Po Web App Standard as this repo applies it:

This repo is one of several independent Po apps. It follows the conventions below, but it owns every file:
nothing is generated from, synced with, or required to stay identical to another repository. Change anything
here freely; only the *conventions* are common, never the code. Azure resources may be shared through the
`PoShared` resource group; code may not.

### Repo skeleton
```
README.md          what the app is, live URL, how to run locally, how it deploys
AGENTS.md          this standard + repo-specific notes (CLAUDE.md just points here)
CLAUDE.md          one line: "Read AGENTS.md."  (plus repo-specific notes only if unavoidable)
<App>.slnx  global.json  Directory.Build.props  Directory.Packages.props  .editorconfig
src/<App>.Api  src/<App>.Client  src/<App>.Shared  [src/<App>.Mobile]
tests/<App>.Unit  tests/<App>.Integration  [tests/<App>.E2E...]
infra/main.bicep  infra/main.bicepparam  [infra/modules/*.bicep]
scripts/           setup / helper scripts (no infra/*.ps1)
docs/              lowercase; everything else that is prose
.github/workflows/deploy.yml   the ONLY workflow
```
No `.cursorrules`, `.copilotignore`, `SPEC.md`, `tasks/`, `DOCS/` at the root (fold useful content into docs/).
`SCREENSHOTS/` is allowed only if README references it.

### Page shell (every page, signed in or out, including the sign-in page)
- Header `.po-header`, height 56px, sticky: app name top-left (`.po-header__title`, links to `/`).
- Top-right `.po-header__actions`: signed-in user's **email** (`.po-header__email`, full email, ellipsis at 16ch on
  phones) — or, signed out, one **Sign in** button `class="po-link-btn filled small"` (same style on every page) —
  then the settings gear ONLY if the app has a settings page (the owner removed settings from some apps — don't
  re-add it). Apps without OAuth show nothing in place of the email (no fake identity chips).
- Version badge `.po-version` fixed bottom-right showing `vYYMMDD`. It must never cover content: `main` gets
  `padding-bottom: calc(var(--po-space-6) + env(safe-area-inset-bottom))`, and when a mobile bottom nav exists the
  badge sits above it (`--po-stamp-lift: <nav height>`).
- Colours only from `wwwroot/css/po-tokens.css` (`--po-*`). All text ≥ 4.5:1 contrast in light AND dark mode
  (status banners: use `--po-text` on `--po-*-soft` backgrounds, never warning-on-peach).
- Login-only data must not be requested for signed-out visitors (no 401s in the console on public pages), and
  actions a signed-out visitor cannot perform are hidden.

### Version
- `Directory.Build.props` stamps ONE assembly metadata key: `BuildVersion` = UTC `yyMMdd`. No `BuildStamp`/`BuildDate`
  aliases. Every reader (header badge component, `/api/diag`, `/health` headers) reads `BuildVersion`.

### Health + /diag contract
- `GET /health/live` (liveness, returns header `X-Commit: <sha>`), `GET /health` (readiness), `GET /api/diag` →
  ```
  DiagReportDto(string Status, string Environment, string Version, DateTimeOffset CheckedAt, double CheckedMs,
                DiagCheckDto[] Checks)
  DiagCheckDto(string Name, string Group /* "api" | "data" | "service" */, string Label, string Status
               /* Healthy|Degraded|Unhealthy */, string Purpose, string Detail, double LatencyMs, string? Problem)
  ```
- UI route `/diag` ONLY (delete other diagnostics pages/routes such as `/system`, `/diagnostics`, `/status`). Page
  `<title>` and `h1`: **System status**. Layout: overall banner in plain words ("Everything is working" /
  "Working, with N things degraded" / "Not working: …"), then three sections in this order: **API**, **Data**,
  **Services**; each row = label, purpose, status pill, latency ms, problem/detail text. Every external dependency
  the app really calls appears as a check. Anonymous, nothing secret in the payload.

### Workflow (`.github/workflows/deploy.yml`, the only one)
push to master → format check → build (Release, warnings as errors) → Unit tests → Integration tests (Testcontainers /
Azurite; Docker is available on ubuntu-latest) → publish → deploy → confirm `/health/live` X-Commit = this SHA.
If the repo has a MAUI `src/<App>.Mobile`, a second job in the SAME file (`mobile`, `needs: []`, not blocking deploy)
installs the `maui-android` workload and builds the Android target, so the client is always compiled.

### Tests
- Unit tier: fast, no I/O, runs in CI.
- Integration tier: runs in CI; locally it SKIPS with a clear message when Docker is not running (never a wall of
  `DockerUnavailableException` failures).
- E2E/UI tiers: run locally against a running app; when no app URL / browser is available they SKIP with a clear
  message. They are not part of CI.
- Docs must state real counts, not caps that are out of date.

### Azure naming
App Service `app-<repo lowercase>`; per-app resources named `<type>-<repo lowercase>`; shared things only in `PoShared`.

## 2. Concurrent work

Several sessions (people and agents) commit to this repo, sometimes at the same time.
- Before every commit and every push: `git fetch origin && git rebase --autostash origin/master`.
- Stage only the files you changed (`git add <paths>`); never `git add -A` / `git add .`; check `git diff --cached`.
- Never revert, reformat or overwrite changes you didn't make; keep the owner's recent decisions (e.g. features
  removed on purpose stay removed — Microsoft sign-in was dropped on 2026-11-20 and stays dropped).
- Don't commit or sync from the IDE while an agent is working in this folder.
- Pushing `master` deploys production; changes to `.github/workflows/*` need a token with the `workflow` scope.

## 3. Verification

| Step | Command (tested 2026-11-20) | Needs / skips |
|---|---|---|
| Format | `dotnet format PoMode.slnx --verify-no-changes` | nothing |
| Build | `dotnet build PoMode.slnx -c Release` (warnings are errors) | kill a running `PoMode.Api` first (DLL locks) |
| Unit | `dotnet test tests/PoMode.Unit` (94) | nothing |
| Integration | `dotnet test tests/PoMode.Integration` (38) | nothing: in-process host + temp folders, no Docker |
| E2EAPI | `dotnet test tests/PoMode.E2EAPI` (25) | nothing (in-process host) |
| E2EUI | `dotnet build PoMode.slnx` then `dotnet test tests/PoMode.E2EUI --no-build` (25) | Playwright Chromium (`pwsh tests/PoMode.E2EUI/bin/Debug/net10.0/playwright.ps1 install chromium`); every test skips with one message without it; boots the Debug build itself on :5199 |
| Run locally | `dotnet run --project src/PoMode.Api` → http://localhost:5000 (`/diag`, `/health/live`) | nothing (file-system storage by default) |
| Deploy | push to `master` → `.github/workflows/deploy.yml` (format, build, Unit, Integration, publish, deploy; `mobile` job builds Android, non-blocking) | — |
| Deploy check | `curl -sI https://app-pomode2.azurewebsites.net/health/live` → `X-Commit: <pushed sha>` | — |
| Live | https://app-pomode2.azurewebsites.net and https://app-pomode2.azurewebsites.net/diag | — |
| Pinned | .NET SDK 10.0.400 (`global.json`, rollForward latestFeature); MAUI `net10.0-android` for `src/PoMode.Mobile` | — |

## This repo

PoMode is a music-analysis app: upload or record a song and it finds the key and mode, sections, chords, beats,
the sung line and stems, explains them, and lets you practise over them. Live: https://app-pomode2.azurewebsites.net

One process: `PoMode.Api` hosts the Blazor WASM client (`PoMode.Client`), the REST endpoints, a SignalR hub
(`/hubs/analysis`) and the background analysis worker; `PoMode.Shared` holds the DTOs, the source-generated
`PoModeJsonContext` and the few pure lookups both sides need; `PoMode.ApiClient` is the one typed HTTP client
for the API, shared by the web client and the mobile app. Server features live under
`src/PoMode.Api/Features/`. `src/PoMode.Mobile` is a native .NET MAUI client on the same API and contracts (not in
`PoMode.slnx`; `scripts/ci-checks.ps1` builds its Android target).

### Commands

```powershell
docker compose up -d                     # local Azurite (blob emulator), only for blob-mirror experiments
dotnet build                             # builds PoMode.slnx (kill any running PoMode.Api first: it locks DLLs)
dotnet run --project src/PoMode.Api      # API + Blazor client at http://localhost:5000 / https://localhost:5001
dotnet format PoMode.slnx --verify-no-changes   # the CI format gate
dotnet test tests/PoMode.Unit            # pure business logic, fast
dotnet test tests/PoMode.Integration     # in-process app + temp-folder storage (no Docker needed)
dotnet test tests/PoMode.E2EAPI          # HTTP contract tests (in-process app)
dotnet test tests/PoMode.E2EUI           # Playwright browser tests (boot the real app from the Debug build)
dotnet test tests/PoMode.Unit --filter "FullyQualifiedName~TempoEstimator"   # one test/class
pwsh scripts/test-caps.ps1               # fails if a suite is over its cap (100/50/25/25)
pwsh scripts/ci-checks.ps1               # local extras: test caps, every infra .bicep compiles, mobile Android build
pwsh scripts/get-models.ps1              # pre-fetch every ONNX/browser model into models/ (warms the E2EUI cache)
dotnet run scripts/layout-audit.cs -- capture before        # app running: screenshot + audit every route
dotnet run scripts/layout-audit.cs -- compare before after notes.md name   # one annotated SCREENSHOTS/name.html
```

### Test tiers (real counts, 2026-11-20)

| Tier | Tests | In CI | Needs |
|---|---|---|---|
| `tests/PoMode.Unit` | 94 | yes | nothing |
| `tests/PoMode.Integration` | 39 | no | nothing (in-process host, temp-folder storage; no Docker) |
| `tests/PoMode.E2EAPI` | 25 | no | nothing (in-process host) |
| `tests/PoMode.E2EUI` | 25 | no | Playwright Chromium; without it every test SKIPS with one clear message (`BrowserFact`). Install: `pwsh tests/PoMode.E2EUI/bin/Debug/net10.0/playwright.ps1 install chromium` |

Suites are capped: 100 Unit, 50 Integration, 25 E2EAPI, 25 E2EUI, counted as the runner counts them (every
`InlineData` row is a test). A new test that would breach a cap has to earn its place against an existing one.

- `ModelAccuracyReportTests` (Integration) renders a known-truth sample MP3, a voice-like stem with vibrato and
  bleed and two drum grooves, races every free pitch/chord/beat executor against them and rewrites
  `test-reports/model-accuracy.html`. A reporting tool, opt-in: `POMODE_MODEL_REPORT=1 dotnet test tests/PoMode.Integration`.
- `InterpreterReportTests` does the same for the written interpretation over every installed Ollama model, into
  `test-reports/interpreter-report.html`, and fails when `OllamaSongInterpreter.PreferredModel` is not the winner:
  `POMODE_LLM_REPORT=1 dotnet test tests/PoMode.Integration --filter InterpreterReport`.

### Shell, health and /diag here

- No sign-in (dropped 2026-11-20): every visitor gets a guest session cookie, and three initials (asked on the
  first visit, `Layout/InitialsPrompt.razor`) open the same collection on any device. The header: `PoMode`
  upper-left, the route links, then search (Ctrl K), the initials button and the settings gear (`/settings`). One header, in `Layout/MainLayout.razor`. Dev personas (`X-Fake-User`,
  Development/Test only) live on `/settings`.
- `/health/live` (X-Commit = `PoPlatform.Commit`), `/health` and `/health/ready` (job storage). `GET /api/diag` is
  the standard `DiagReportDto` (`PoMode.Shared/Diagnostics/DiagDtos.cs`, built by `DiagnosticsService`): API,
  Data (job storage), Services (Key Vault, AI interpreters, analysis models, push notifications).
  `GET /api/diag/activity` carries the operator extras (queue depth, guards, interpreter record, the plan a new
  upload would run). `DiagnosticsReportTests` guards that /api/diag names no secret, path or host.
- Version: `Directory.Build.props` stamps `BuildVersion`; the badge (`Layout/BuildVersion.razor`), `/api/diag` and
  `/health/live` read it.
- API reference UI: `/scalar` (Development only).

### Deviations from the standard

| What | Why |
|---|---|
| Azure names `app-pomode2`, `stpomode2`, `asp-pomode-f1` (standard: `app-pomode`, `<type>-pomode`) | Existing production resources; renaming needs new resources and a cut-over nobody has asked for yet. `infra/main.bicep` uses the live names. |
| `tests/PoMode.TestCommon` (linked `.cs` files, not a project) | Test helpers used by this repo's own test tiers only. |
| `models/` at the root | Local cache of downloaded ONNX/browser models (`scripts/get-models.ps1`); contents are git-ignored. |
| No `docs/` | Nothing to put there yet; prose lives in this file and README.md. |
| The header shows the guest's initials (standard: nothing without OAuth) | The initials are the library's key, not a fake identity: they are how a person finds their songs on another device. |
| `deploy.yml` runs format, build, Unit, publish, deploy; no Integration step, no `mobile` job | Kept lean on the F1 plan; Integration, E2E, Bicep and the mobile build run locally (`scripts/ci-checks.ps1`). |

### Working rules

- **Run only the tests that cover what you changed** (`--filter`) while iterating; run every tier above before a
  push. Say what you did not verify.
- **Build, restart, then check the restart worked**: `/health/ready` answering is the check. Kill any running
  `PoMode.Api` before building; it locks the DLLs.
- **An unused `using` fails the build** (IDE0005 + warnings as errors); delete it rather than suppress it.
- **Work on `master`.** Commit messages are short and casual, the way a person types them.
- **No `dotnet user-secrets`, ever.** Configuration goes in `appsettings*.json`; secrets in Azure Key Vault, read
  through `SecretsBootstrap` / `DefaultAzureCredential`. Never connection strings in `appsettings`.
- **Run the command yourself** (`az`, `gh`, Bicep) rather than handing the user steps or portal clicks.
- **UI changes get a before/after page**: `SCREENSHOTS/{YYMMDD}-{feature}.html` plus a side-by-side PNG (and
  README.md references `SCREENSHOTS/` once it exists).
- **Docs move with the code**: a changed dependency, configuration key or major endpoint updates this file and
  README.md in the same change.
- **Answers over ~100 words end with a one-line TLDR**; a code change over 20 lines gets the three-level TLDR
  (Toddler / Child / Adult); over 100 lines, end with three questions about direction.

### Conventions (NET_RULES as applied here)

- `Po` prefix everywhere; .NET 10, Central Package Management, nullable + warnings-as-errors.
- Minimal API, vertical slices (`Features/{Name}`), `MapGroup()`, OpenAPI + Scalar in Development.
- `FakeAuthHandler` (`X-Fake-User`, `X-Fake-Roles`) only outside Production; it throws in Production.
- Managed identity / `DefaultAzureCredential` + Key Vault (`kv-poshared2` in `PoShared`) for every secret.
- UI: Radzen Blazor, no inline CSS (scoped `.razor.css` + tokens). `po-tokens.css` is linked first; `pm-tokens.css`
  aliases PoMode's own tokens (music palette, key hue) to `--po-*`. Light/dark follow the device only.
- Tab with nothing focused opens the developer window `/po-dev.html` (`js/po-devwindow.js`, `js/po-dev.js`);
  `FocusOnNavigate` runs only after the first in-app navigation so a fresh page has nothing focused.
- "USING MOCK DATA" banner whenever a result came from a fake/placeholder executor.
- Every form validates with a FluentValidation validator in `PoMode.Shared/Validation`, shows the message inline
  and disables its submit while busy; the endpoint runs the same rule and returns `ValidationProblem`.
- Ports: HTTP 5000, HTTPS 5001. Home page title: exactly `PoMode`.

### CI/CD and Azure

- `.github/workflows/deploy.yml` (the only workflow): push to `master` → format → build → Unit → publish →
  deploy to `AZURE_WEBAPP_NAME` → wait for `/health/live` X-Commit = the pushed sha. Integration, E2E, Bicep and
  the mobile build run locally before a push.
- Azure sign-in is GitHub OIDC; repo **variables** `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`,
  `AZURE_WEBAPP_NAME`, `AZURE_RESOURCE_GROUP`. Never secrets, never resource names in the workflow.
- Every permission the app and CI need lives in `scripts/ensure-azure-access.ps1`; a role granted by hand and not
  added there is a bug.
- `infra/main.bicep` + `infra/main.bicepparam`, modules in `infra/modules/`; applied by hand
  (`az deployment sub create -l eastus2 -f infra/main.bicep -p infra/main.bicepparam`), never by CI.
