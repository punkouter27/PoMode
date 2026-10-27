# AGENTS.md

Rules for any coding agent working in this repository. `CLAUDE.md` ("Working rules") carries the
commands and the longer versions of these rules; the architecture and the decisions already made are
in `docs/`. Read both before changing anything.

## 1. Branching & version control

- **Master first.** Work only on `master`; branch only when explicitly asked.
- **Human-like commits.** When the user says commit or `git sync`, stage everything, commit with a
  terse, offhand American-slang message that feels human ("fix the busted tempo math"), and push.
  Never commit or push unprompted.

## 2. Runtime, testing & quality

- **Startup verification.** After any code change, kill the running `PoMode.API`, `dotnet build`,
  restart it, and confirm `/health/ready` answers.
- **Targeted testing.** Run only the tests scoped to the code you changed (`--filter`); skip tests for
  minor edits (a comment, a label, a CSS value). Never run the full suite.
- **Zero warnings.** `TreatWarningsAsErrors=true` is set in `Directory.Build.props`; fix every warning
  as it appears (an unused `using` included) rather than suppressing it.

## 3. Architecture & configuration

- **Documentation sync.** Check `docs/` and `docs/README.md` first for architectural context. When a
  dependency, a configuration key or a major endpoint changes, update both in the same change.
- **Full automation.** Do every build, run and environment step yourself (`dotnet`, `az`, `gh`,
  Bicep, scripts). Do not hand the user CLI commands or portal click-paths to carry out.

## 4. UI changes & reporting

- **Visual artifacts.** For a UI change, screenshot before and after (Playwright), annotate what
  changed, save both into `SCREENSHOTS/{YYMMDD}-{feature}.html` at the repo root, and give the valid
  absolute path in the reply.
- **Build version watermark.** The layout shows the build version as `YYMMDD` (from the build date or
  the Git commit date), fixed in the bottom-right corner.
- **Conditional probing.** If a code change exceeds 100 lines and the user's prompt has no pending
  questions, end with exactly 3 strategic questions about the app's direction and follow-on changes.
- **Response footers.**
  - *TL;DR:* a response over 100 words ends with a TL;DR of 20 words or fewer.
  - *Diff summary:* a code change over 20 lines gets a 3-tier summary, one sentence each:
    1. Toddler: the simple idea.
    2. Child: what it does.
    3. Adult: the technical and architectural summary.
