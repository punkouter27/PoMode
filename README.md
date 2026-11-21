# PoMode

PoMode is a .NET 10 music-analysis application. Its API host serves a Blazor WebAssembly
client, accepts audio-analysis jobs, and coordinates background workers, browser-assisted
analysis, library management, and optional song interpretation.

Live: https://app-pomode2.azurewebsites.net (status page: `/diag`).

## Architecture

The rules, the decisions and the commands are in [`AGENTS.md`](AGENTS.md).

- `src/PoMode.Api/` hosts the client, Minimal API endpoints, SignalR analysis hub, and
  background analysis worker. Server features live in `Features/<Feature>/`.
- `src/PoMode.Client/` contains the Blazor WebAssembly UI.
- `src/PoMode.Shared/` contains contracts and source-generated JSON support shared by the
  API and client.
- `src/PoMode.ApiClient/` is the one typed HTTP client for the API, used by the web client and
  the mobile app.
- `src/PoMode.Mobile/` is the native .NET MAUI app (outside the solution; `scripts/ci-checks.ps1`
  builds it).
- `tests/` contains Unit (94), Integration (39), E2E API (25) and Playwright E2E UI (25) projects.

The solution is `PoMode.slnx`. `AGENTS.md` is the detailed guide: the Po web standard this repo
follows, the architecture, constraints, test tiers and development workflow.

## Local development

Use the .NET SDK pinned by `global.json`. Start the local Azurite emulator if you want the
blob mirror (the default is file-system storage), then run the host:

```powershell
docker compose up -d
dotnet run --project src/PoMode.Api
```

The app serves on `http://localhost:5000` and `https://localhost:5001`. The Scalar API
reference is at `/scalar`; `/health`, `/health/live`, and `/health/ready` expose health
status.

Every page carries the Po shell: the name top-left, your three initials and the settings
gear top-right, and the build stamp `vYYMMDD` (UTC commit date) bottom-right. There is no sign-in and
nothing is private: a first visit asks for three initials, and those letters open the same collection
of songs on any device.
`/diag` ("System status") shows one row per dependency (API, Data, Services) for anyone, from the
anonymous `GET /api/diag`. Pressing Tab before
anything has focus opens a developer window (`/po-dev.html`) with live health and page stats.
Light and dark follow the device.

## Phone app

`src/PoMode.Mobile/` is a native .NET MAUI app (Android, iOS) on the same API and the same
`PoMode.Shared` contracts. It is not in `PoMode.slnx`; CI's `mobile` job builds its Android
target on every push (never blocking the deploy), and by hand:
`dotnet build src/PoMode.Mobile -f net10.0-android` (needs the `maui-android` workload). It opens with a persistent guest session.

- **Songs:** pick or record a song; it goes up through the same tus endpoint in 4 MB chunks and
  resumes after a dropped connection or a restart. A foreground service (Android) or background task
  (iOS) keeps it going with the screen locked, and a notification opens the song when the analysis
  is done (`pomode://song/{jobId}`).
- **Song:** live progress over `/hubs/analysis`; then the mode and why, sections, bar-by-bar chords,
  guitar shapes and the loop, voice and stems playback with a voice/band mix, statistics, the
  written summary and questions (streamed), MIDI, re-run with another model, delete.
- **Practice:** pick a home note, mode and chords and sing over them. The phone tracks your pitch
  (YIN) and draws your line over the chord tones live, with a vibrato readout. The take goes to
  `/api/modal-melodies/hum`. A Bluetooth LE or USB MIDI keyboard can play the take instead.
  Also on Practice: earlier takes and your range, and the guitar jam (strummed on the phone,
  sing over it).
- **Drone and click:** a drone on any note and a metronome that keep playing with the screen
  locked, with play/pause and ±5 BPM on the lock screen.
- **Mode Lab:** generate, play (`/api/modal-melodies/wav`), describe a change in words, analyse
  it, share as MIDI/WAV.

Local loop: start the API, then `pwsh scripts/mobile-dev.ps1 -Run` (`adb reverse` maps the
phone's `localhost:5000` to the API). Not on the phone yet: the Mode Lab's drum strip and gravity panel, and the
similarity-matrix view of how the sections were found.

## Deploy

A push to `master` runs `.github/workflows/deploy.yml`: format check, build, Unit tests, publish,
deploy, then wait for `/health/live` to report the new commit. Integration and E2E tests, the test
caps, Bicep and the mobile build run locally: the related tests and `scripts/ci-checks.ps1` before
pushing. Azure permissions all come from `scripts/ensure-azure-access.ps1`.

## Verification

Run the tests relevant to a change while iterating and every tier before a push. No tier needs
Docker; E2E UI tests need Playwright Chromium and skip with a clear message without it.

```powershell
dotnet build PoMode.slnx
dotnet test tests/PoMode.Unit --filter "FullyQualifiedName~TempoEstimator"
pwsh scripts/test-caps.ps1
```

See `AGENTS.md` for targeted test commands, layout auditing, model setup, and the
project-specific verification limits.
