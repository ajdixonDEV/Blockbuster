# Blockbuster Remediation Continuation Plan

## Checkpoint

The repository is intentionally left at a buildable, unit-tested checkpoint. Treat the current
working tree as the baseline and preserve every pre-existing user change, including the catalog-page
replacement, navigation/UI work, shared-room behavior, and drag-prevention changes.

Validated at this checkpoint:

- `dotnet build Blockbuster.slnx --no-restore` passes with no warnings.
- `dotnet test Blockbuster.slnx --no-build` passes all 48 tests.
- `npm run format:check` passes.
- `npm run check:readability` is installed and currently reports 30 remaining long lines; this is an
  expected unfinished item, not a reason to weaken the check.

## Completed Work

### Endpoint security and composition

- Split endpoint mapping into authentication, administration, playback, shared-room, and health
  modules.
- Applied the administrator policy once to the `/admin` route group and removed repeated manual
  authentication calls.
- Configured antiforgery to accept `X-CSRF-TOKEN`, rendered the request token in the root document,
  added `<AntiforgeryToken />` to POST forms, and attached antiforgery metadata to mutation
  endpoints.
- Removed the unused JSON `/api/shared` endpoint while retaining `/shared/new`.

### Playback and process lifecycle

- Made `playerController.js` the shared implementation for controls, keyboard input, timeline,
  volume, mute, fullscreen, status, and ARIA state.
- Added playback, seek-completion, buffering, and fullscreen hooks used by shared playback.
- Added one serialized optimistic `progressWriter.js` with antiforgery headers, conflict handling,
  and an awaitable final flush.
- Made direct and shared player disposal await progress flush, controller cleanup, and shared-hub
  shutdown.
- Extracted `ExternalProcessRunner`; timeout and caller cancellation kill the process tree and await
  process/stream completion. Added passing timeout and cancellation tests.

### Scanning and catalog persistence

- Replaced `IMovieCatalogStore` with the match-focused `IMovieMatchTransitionStore`.
- Removed direct scan mutation models/methods and automatic resolution from the Core public
  contract.
- Kept scan-run, observation, promotion, failure, and recovery persistence inside Infrastructure.
- Added one transaction-aware transition writer shared by manual and staged match updates.
- Stages observations in one explicit transaction through a prepared command.
- Promotes media facts with one set-based `INSERT ... SELECT ... ON CONFLICT` statement and performs
  per-item work only for staged match-resolution payloads.
- Combines failed-run completion and staging cleanup in one transaction.
- Removed the three inert configuration options and the empty shared-room component without changing
  migrations or data formats.

### Tooling

- Added pinned Prettier 3.6.2, root EditorConfig/Prettier configuration, formatting scripts, and the
  authored-source 160-character readability check.
- Added a Node Playwright launcher that chooses one free loopback port and one unique data root,
  then exports stable values consumed by `playwright.config.js`.

## Remaining Work

### 1. Finish the authored-source readability pass

Run `npm run check:readability` and reflow every reported line. Do not add per-file exceptions. The
remaining files are concentrated in:

- `Blockbuster/Components/Layout/MainLayout.razor`
- `Blockbuster/Components/Pages/About.razor`
- `Blockbuster/Components/Pages/Admin.razor`
- `Blockbuster/Components/Pages/Profiles.razor`
- `Blockbuster/Shared/SharedPlaybackHub.cs`
- `Blockbuster.Core/Playback/MoviePlayback.cs`
- `Blockbuster.Infrastructure/Movies/TmdbMovieMetadataProvider.cs`
- `Blockbuster.Infrastructure/Profiles/ProfileStore.cs`
- `Blockbuster.Infrastructure/Shared/InMemorySharedPlaybackCoordinator.cs`
- the matching, persistence, scanning, and browser test files named by the check

Also inspect for compressed constructs below 160 characters, especially one-line Razor branches,
multi-statement test cleanup blocks, and compact conditionals. Reformat them without changing
behavior. Run `dotnet format Blockbuster.slnx --no-restore` after manual C#/Razor cleanup.

### 2. Complete browser and endpoint coverage

- Update every direct progress `fetch` in `tests/browser/application.spec.js` to read the rendered
  `meta[name="csrf-token"]` value and send `X-CSRF-TOKEN`.
- Add assertions that an authenticated mutation without a token returns 400 while valid profile and
  administrator form submissions succeed.
- Extend the browser-level player tests to cover the shared controller hooks, buffering transitions,
  serialized progress ordering, conflict handling, and awaited disposal. Prefer testing
  `progressWriter.js` directly with a deterministic mocked `fetch` queue for ordering/disposal.
- Run `npm run test:browser` and keep exactly the seven real-browser scenarios passing unless a new
  scenario is genuinely necessary; coverage can be added to existing scenarios.
- Investigate behavior rather than weakening assertions if shared-room timing or antiforgery causes
  a failure.

### 3. Finish scan acceptance coverage

- Add an explicit multi-file promotion test that inserts several changed/new observations in one run
  and verifies all facts and associations after the set-based path.
- Retain the existing unchanged-facts, missing-file, cancellation-cleanup, recovery, and
  forced-rollback tests.
- Confirm the failure cleanup remains atomic if promotion throws.

### 4. Reconcile documentation and the root plan

- Update `docs/IMPLEMENTATION.md` so the completed and next milestones are chronological and
  describe the endpoint security, canonical player, process runner, and set-based reconciliation
  work accurately.
- Remove remaining references to `IMovieCatalogStore` and the deleted inert options outside the
  source plan.
- Restore `PLAN.md` byte-for-byte from `C:\Users\adixo\Downloads\PLAN (7).md`. Prettier reformatted
  it before it was added to `.prettierignore`, so it currently does not match the attachment. Keep
  `PLAN.md` in `.prettierignore` afterward to preserve the required verbatim artifact.
- Run Prettier on this continuation plan if it is edited.

### 5. Final acceptance

Run all commands from the repository root in this order:

```powershell
dotnet format Blockbuster.slnx --no-restore
dotnet format Blockbuster.slnx --no-restore --verify-no-changes
dotnet build Blockbuster.slnx --no-restore
dotnet test Blockbuster.slnx --no-build
npm run format:check
npm run check:readability
npm run test:browser
```

Require zero formatting/readability violations, a warning-free build, all .NET tests passing, and
all seven browser tests passing. Finish by reviewing `git diff --check` and `git status --short`; do
not discard or overwrite unrelated user changes.

## Known Risks to Check First

- Antiforgery metadata compiles, but the full browser flow has not yet been exercised after the
  route split. Verify cookie/token emission and form redirects in the real app.
- The shared player now composes controller events instead of replacing DOM handlers. Verify that
  remote state application remains suppressed while local play/pause/seek commands still reach the
  hub exactly once.
- The set-based promotion SQL passes current tests, but the explicit multi-file acceptance case
  remains to be added.
- `dotnet format` intentionally touched many authored C# files. Preserve those formatting-only
  changes while reviewing any file that also contained user work.
