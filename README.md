# PoMode

PoMode is a .NET 10 music-analysis application. Its API host serves a Blazor WebAssembly
client, accepts audio-analysis jobs, and coordinates background workers, browser-assisted
analysis, library management, and optional song interpretation.

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

Every page carries the Po shell: the name top-left, the email (or Sign in) and the settings
gear top-right, and the build stamp `vYYMMDD` (UTC commit date) bottom-right. `/diag` shows one
card per dependency (API, Data, Services) for anyone, signed in or not. Pressing Tab before
anything has focus opens a developer window (`/po-dev.html`) with live health and page stats.
Light and dark follow the device.

## Phone app

`src/PoMode.Mobile/` is a native .NET MAUI app (Android, iOS) on the same API and the same
`PoMode.Shared` contracts. It is not in `PoMode.slnx` or CI; build it by hand
(`dotnet build src/PoMode.Mobile -f net10.0-android`). It opens with a persistent guest session.

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

A push to `master` runs `.github/workflows/deploy.yml` (the same file in every Po repo): build,
publish, deploy, then wait for `/health/live` to report the new commit. It runs no tests;
run the related tests and `scripts/ci-checks.ps1` locally before pushing.

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
