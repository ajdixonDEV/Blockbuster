# Implementation status

This file is the short handoff for future implementation conversations. The detailed product plan remains the source of truth; Git history records completed work.

## Working agreement

1. Implement one bounded milestone per conversation.
2. Keep the solution building and tests passing at every milestone boundary.
3. Update this file with completed work, decisions, and the next milestone.
4. End each completed milestone with a focused Git commit.

## Milestones

- [x] 01 — Repository and application scaffold
- [x] 02 — Typed storage/configuration options and path resolution
- [x] 03 — Serilog startup, rolling files, and request logging
- [x] 04 — SQLite connection factory and DbUp migration runner
- [x] 05 — Data-protection persistence and health checks
- [x] 06 — Windows Service/systemd hosting and operator documentation
- [x] 07 — Profiles and administrator foundation
- [x] 08 — Shared media primitives and movie scanning
- [x] 09 — Movie catalog and direct playback
- [x] 10 — Shared movie rooms
- [x] 11 — Shared-room lifecycle hardening
- [x] 12 — Atomic configured-root reconciliation
- [x] 13 — Catalog contract and resolver verification hardening
- [ ] 14 — Browser playback end-to-end verification

## Completed in milestone 01

- Converted the worker project to a .NET 10 Blazor Web App.
- Enabled global Interactive Server rendering.
- Established `Blockbuster.Core`, `Blockbuster.Infrastructure`, and `Blockbuster.Tests` projects.
- Added media-neutral core primitives without speculative type-specific schemas.
- Added deterministic SDK selection, shared compiler policy, ignore rules, and baseline tests.
- Added routed placeholder catalogs for Movies, TV, Videos, and Music.

## Completed in milestone 02

- Added typed options for storage, libraries, scanning, media probing, TMDB,
  playback, history, rooms, and authentication.
- Bound and validated all options at startup, including absolute storage and
  media-root paths, stable unique library IDs, timing constraints, history caps,
  and optional four-digit bootstrap PINs.
- Added a storage path resolver with database, artwork, cache, generated-file,
  log, backup, and data-protection defaults beneath one absolute data root.
- Added safe committed base, Development, and Production configuration. Local
  development derives its ignored `.data` directory from the content root;
  production must supply its data root externally.
- Documented environment-variable overrides for machine paths and secrets.
- Added configuration binding, validation, path default, path override, stable
  library ID, and leading-zero PIN tests.

## Completed in milestone 03

- Added a bootstrap logger before host construction so startup and configuration
  failures reach the console.
- Integrated Serilog with ASP.NET Core and `ILogger<T>` using the pinned
  `Serilog.AspNetCore` 10.0.0 package.
- Added invariant-format console output and daily rolling files beneath the
  resolved logs path, with 50 MB size rolling and 14 retained files.
- Added application and environment context plus one completion event per HTTP
  request. Query strings are excluded from the request event.
- Reduced successful media-range and Blazor transport requests to Debug while
  preserving warnings and errors, and raised noisy framework categories to
  Warning.
- Added fatal startup logging and asynchronous clean-shutdown flushing.

## Completed in milestone 12

### Milestone 12 — Atomic configured-root reconciliation

- Introduce `IConfiguredRootReconciler.ReconcileAsync(sourceId, rootPath, cancellationToken)`.
- Reduce `LibraryScanner` to scheduling, configured-root iteration, aggregation,
  and status reporting.
- Use migration 005's run-scoped observations to stage every discovered file,
  probe result, assigned media ID, and match-resolution payload before changing
  live catalog rows.
- Promote one completed root in a single SQLite transaction: apply changed files
  and match transitions, mark absent paths unavailable, complete run/state, and
  delete the staging rows.
- On cancellation or failure, mark the run unsuccessful and delete staging rows
  while preserving live availability, associations, metadata, overrides, and
  pending matches.
- Recover at startup by marking incomplete runs interrupted and removing orphaned
  observations before the first scan.

## Milestone 12 progress

- Introduced `IConfiguredRootReconciler` as the sole configured-root traversal
  boundary; `LibraryScanner` now only schedules, iterates configured roots, and
  reports aggregate status.
- Added migration 005's run-scoped observation table and stage every discovered
  media path, facts, probe result/error, and existing media-file assignment
  before any root promotion work begins.
- Promotion now uses one SQLite transaction to apply staged media facts, mark
  absent paths unavailable, complete the run and configured-root state, and
  remove the staged rows together.
- Automatic TMDB matching and independent artwork caching now run while the
  root is being observed; their typed metadata or pending-review payload is
  persisted with each observation and applied in that same promotion
  transaction. Failed roots therefore cannot change associations, metadata,
  overrides, or pending-review state.
- Kept a failed or cancelled traversal isolated from availability reconciliation:
  its run is completed unsuccessfully and its staged observations are removed.
- Added stale-run recovery to both hosted-service startup and scan invocation,
  marking interrupted runs unsuccessful and removing their observations.
- Split the catalog's administration query surface into `IMovieCatalogReader`;
  the administration page no longer receives scan mutation APIs.
- Added integration coverage for successful promotion, unavailable-root
  preservation, cancellation cleanup, stale-run recovery, and a forced SQLite
  trigger rollback after promotion starts.

## Completed in milestone 13

- Separated the `IMovieCatalogReader` administrative query contract from the
  mutation-only `IMovieCatalogStore`; the administration route only receives the
  read contract, while scanning and matching receive mutations explicitly.
- Verified staged reconciliation promotion, unavailable-root preservation,
  cancellation cleanup, interrupted-run recovery, and transaction rollback.
- Confirmed the five-script migration upgrade, including its staging table, in
  the persistence integration suite.

## Next milestone

### Milestone 14 — Browser playback end-to-end verification

- Add a serial Playwright 1.62.1 Chromium suite that starts the real app on
  loopback with a disposable ignored data root, waits for migrations, seeds two
  profiles and a movie/version, and serves the committed tiny MP4 fixture.
- Cover direct controls/keyboard input, progress saves and conflicts, two-context
  shared joining, participant updates, play/seek sync, reconnect/rejoin,
  local-only volume/mute, and disposal removing room membership.
- Capture Playwright traces and screenshots on failure; run the suite after the
  playback/realtime milestone and again after reconciliation is complete.

## Milestone 14 progress

- Added pinned Playwright configuration with one serial worker, disposable
  ignored data root, real loopback application startup, and traces/screenshots
  retained on failure.
- Added a passing real-app smoke test for migration/startup and the profile
  selection route. Chromium is installed locally for this test environment.
- Added browser-level controller coverage for media-control ARIA state, Space
  and mute shortcuts, and entering and exiting fullscreen with the real module
  loaded from the running application.

## Completed in milestone 11

- Replaced connection-ID-based coordinator calls with connection-scoped shared-room
  sessions. A session is the only command authority and its leave operation is
  idempotent, so a disconnected or unjoined caller cannot create membership.
- Made room state, membership, expiry, and removal share one synchronization
  boundary. Cleanup now uses injected `TimeProvider`, including deterministic
  expiry-boundary and rejoin tests.
- Made hub group-join failures roll back membership and disconnect close the stored
  session before broadcasting the updated participant snapshot.
- Extracted the duplicated Razor video/control surface used by direct and shared
  playback, retaining the existing DOM hooks and routes while reducing markup drift.
- Added a canonical movie-match resolver. Scanner and administrator flows now use
  the same provider/local resolution policy; poster and backdrop caching run in
  parallel and fail independently without discarding metadata.

## Completed in milestone 10

- Added in-memory, media-neutral shared-room snapshots with pinned movie versions,
  serialized last-command-wins revisions, participant membership, active-room
  discovery, and configured empty-room expiry. Rooms intentionally end on restart.
- Added an authenticated SignalR hub for membership, compact state/control messages,
  disconnect handling, and reconnect snapshots. Any joined profile can play, pause,
  or seek, and the latest controller is shown to everyone.
- Added shared-room creation from compatible movie versions, a discovery route, and
  a user-gesture-gated shared player. Each browser independently range-streams the
  pinned version and retains local volume, mute, fullscreen, buffering, and errors.
- Added periodic drift checks with modest playback-rate correction and hard seeks,
  pinned-version compatibility warnings, blocked-autoplay recovery messaging, and
  independent per-profile playback progress/history updates.
- Added coordinator coverage for revision ordering, reconnect snapshots, creator
  departure, participant membership, and invalid-command rejection.

## Completed in milestone 07

- Added migration-backed profiles and a singleton administrator credential with
  repository contracts in Core and Dapper implementations in Infrastructure.
- Added exactly-four-digit PIN validation and PBKDF2-SHA256 hashing with random
  salts and fixed-time verification. Leading zeroes remain significant.
- Bootstrap the first administrator credential from deployment configuration,
  ignore that secret once a credential exists, and activated the server-local,
  non-echoed administrator PIN reset command.
- Added independent encrypted, HTTP-only profile and administrator cookies.
  Profile selection is session-only; the administrator cookie uses the configured
  lifetime and never derives authority from a viewing profile.
- Guarded all catalog routes behind profile selection and added profile selection,
  administrator sign-in/sign-out, and administrator-only profile CRUD.
- Added administrator health and read-only configuration summaries. Host settings
  remain file-managed.
- Clearly label all PINs as unthrottled convenience barriers suitable only for a
  trusted LAN or VPN.

## Completed in milestone 08

- Added migration-backed configured-root scan state and runs, shared media files
  with file facts and probe data, movies, provider metadata, local overrides,
  genres, artwork paths, movie versions, and pending match review records.
- Added explicit `IMediaProbe`, `IMovieMetadataProvider`, `IArtworkCache`,
  `IMovieCatalogStore`, and `ILibraryScanner` contracts around the existing
  media-neutral primitives.
- Added bounded, shell-free ffprobe execution and structured duration, container,
  video/audio codec, resolution, and channel parsing. Per-file probe failures are
  retained for administrator review without aborting a successfully traversed
  root.
- Added filename title/year parsing and strict TMDB matching that accepts only one
  normalized-title and matching-year result. Missing-year, unmatched, ambiguous,
  unavailable-provider, and corrupt-probe cases remain explicit review items.
- Added bearer-authenticated TMDB search/details, locally cached poster/backdrop
  artwork, multi-file version merging by confirmed TMDB identity, and separate
  administrator overrides that rescans do not overwrite. Added an About/Credits
  route with TMDB's approved logo, link, and required non-endorsement notice.
- Added non-overlapping startup, six-hour scheduled, and administrator-requested
  scans with configurable concurrency. Each configured root reconciles
  independently; unavailable or materially failed roots never mass-mark media
  missing, while successful scans retain unavailable file associations/history.
- Added administrator scan status/history and pending-match review with candidate,
  direct TMDB ID, and local-metadata resolution paths.
- Added unit/integration coverage for filename parsing, confidence matching, TMDB
  request behavior, fresh/upgraded migrations, new/changed/missing files,
  duplicate versions, corrupt probes, ambiguous matches, missing years, override
  preservation, and failed-root availability protection.

## Completed in milestone 09

- Replaced the Movies placeholder with a paged poster catalog supporting text,
  genre, and year filters plus title, release-year, and recently-added sorting.
- Added movie detail pages with cached artwork, effective local/provider metadata,
  version facts, availability, resume actions, and conservative browser direct-play
  compatibility messaging. Incompatible titles remain visible and explain that
  transcoding is not available.
- Added profile-authorized artwork and media-file-ID endpoints. Media resolution
  is constrained beneath the scanned root and ASP.NET Core range processing
  supplies GET/HEAD, partial, suffix, open, and invalid-range behavior without
  routing bytes through the Blazor circuit.
- Added a small JavaScript `HTMLMediaElement` wrapper with custom play, seek,
  volume, mute, fullscreen, loading/error states, keyboard shortcuts, and resume.
- Added migration-backed per-profile movie progress with optimistic server-issued
  revisions and ten-second/pause/navigation/end saves. Playback events are capped
  per profile and no watched/completed state is inferred.
- Added integration coverage for progress revision conflicts, accepted updates,
  event ordering/trimming, and the upgraded four-script migration set.

## Completed in milestone 04

- Added the explicit `IDbConnectionFactory` contract and a pooled
  `Microsoft.Data.Sqlite` implementation that opens one connection per operation.
- Enabled foreign keys and a five-second busy timeout on every opened connection.
- Added startup migration through pinned DbUp SQLite 6.0.4 with immutable,
  numbered embedded scripts, one transaction per script, and DbUp's journal.
- Enabled and verified WAL mode before migration, and made migration failure abort
  application startup.
- Added the first foundation migration and integration coverage for fresh and
  repeated upgrades, journal behavior, required pragmas, and concurrent writes.

## Completed in milestone 05

- Persisted ASP.NET Core data-protection keys beneath the resolved data root and
  isolated them with the stable `Blockbuster` application name.
- Added `/health/live` for process liveness and `/health/ready` for structured
  readiness details.
- Added readiness checks for SQLite integrity, writable generated-state
  directories, ffprobe availability, configured media roots, and TMDB setup.
- Kept a missing TMDB token degraded while treating missing required ffprobe or
  unavailable storage/database dependencies as unhealthy.
- Added integration coverage proving protected values survive provider recreation
  and readiness becomes healthy with all dependencies configured.

## Completed in milestone 06

- Added context-aware Windows Service and Linux systemd hosting lifetimes while
  retaining normal console behavior for development.
- Added validated, opt-in forwarded-header handling that requires explicit trusted
  proxy IP addresses and supports HTTPS termination through Caddy or Nginx.
- Added a local operator command dispatcher and a consistent timestamped SQLite
  backup command using SQLite's online backup API. Existing files are not
  overwritten and backups remain externally scheduled.
- Added the non-echoed, interactive administrator PIN reset command contract. It
  intentionally remains inactive until milestone 07 supplies the administrator
  credential store.
- Documented framework-dependent publishing, Windows Service registration,
  systemd hardening and permissions, reverse proxies, health endpoints, secrets,
  and operator commands in `docs/OPERATIONS.md`.
- Added tests for reverse-proxy trust validation and consistent backup snapshots.

## UI skeleton

- Added Blazor Blueprint Components 3.15.0 and Lucide Icons 2.0.2 as the UI
  foundation, including services, precompiled styles, theme variables, imports,
  and the required root portal host.
- Added a responsive retro rental-store shell, home hero, Blueprint library
  cards, and routed catalog empty states for Movies, TV, Videos, and Music.
- Added a Blueprint movie toolbar skeleton. Controls that depend on future
  persistence and profile work remain disabled rather than implying behavior.
