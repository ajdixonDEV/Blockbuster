# Implementation status

This is the short handoff for future implementation work. `PLAN.md` is the remediation source of
truth, while Git history records completed product milestones.

## Working agreement

1. Preserve existing behavior, migrations, on-disk data, and unrelated user changes.
2. Keep the solution buildable and tested at each milestone boundary.
3. Update this file when completed work or the next milestone changes.
4. Finish implementation work with the full repository acceptance sequence.

## Product milestones

- [x] 01 — Repository and application scaffold
- [x] 02 — Typed storage/configuration and path resolution
- [x] 03 — Structured startup, request, console, and rolling-file logging
- [x] 04 — SQLite connection factory and migration runner
- [x] 05 — Data-protection persistence and health checks
- [x] 06 — Windows Service/systemd hosting and operator commands
- [x] 07 — Profiles and administrator authentication
- [x] 08 — Media probing, movie scanning, matching, and review
- [x] 09 — Movie catalog and direct playback
- [x] 10 — Shared movie rooms
- [x] 11 — Shared-room lifecycle and resolver hardening
- [x] 12 — Atomic configured-root reconciliation
- [x] 13 — Catalog contract and resolver verification
- [x] 14 — Real-browser playback verification
- [x] 15 — Security, lifecycle, persistence, and readability remediation

## Completed foundation and operations work

- Built a .NET 10 Blazor Web App with global Interactive Server rendering and separate Core,
  Infrastructure, web, and test projects.
- Added validated options and a storage-path resolver for database, artwork, cache, generated files,
  logs, backups, and data-protection keys. Deployment secrets and machine paths remain external.
- Added Serilog startup and request logging, daily size-limited rolling files, health probes,
  persisted data-protection keys, service hosting, trusted-proxy validation, backup, and local
  administrator reset commands.
- Added a pooled SQLite connection factory and immutable numbered migrations. WAL mode, foreign
  keys, busy timeouts, migration upgrades, integrity checks, and concurrent writes are covered by
  integration tests.

## Completed identity and endpoint work

- Added migration-backed viewer profiles and administrator credentials, four-digit PIN validation,
  PBKDF2 hashing, separate encrypted cookies, and administrator-only profile management.
- Split endpoint composition into authentication, administration, playback, shared-room, and health
  modules. Administrator mutations share one authorized `/admin` route group.
- Enabled antiforgery validation for every mutation. Forms render antiforgery fields, the root
  document publishes a request token for JavaScript, and JSON progress writes send `X-CSRF-TOKEN`.
- Removed the unused JSON shared-room endpoint while retaining authenticated form-based room
  creation.

## Completed scanning and catalog work

- Added configured-root scan state, runs, media facts, movies, provider metadata, local overrides,
  genres, artwork, versions, and pending-match review without changing existing migrations.
- Added shell-free media probing and strict TMDB matching. Missing years, unavailable metadata,
  ambiguous results, unmatched titles, and corrupt probes remain explicit review outcomes.
- Reduced `LibraryScanner` to scheduling and configured-root orchestration. Infrastructure stages a
  root's observations in one transaction and promotes it atomically.
- Replaced the broad mutation surface with `IMovieMatchTransitionStore`. Manual and staged match
  changes use the same transaction-aware transition writer.
- Promotes media facts with a set-based `INSERT ... SELECT ... ON CONFLICT` operation. Per-item work
  remains only for staged match-resolution payloads.
- Failed-run completion and staging cleanup are atomic. Tests cover unchanged facts, missing files,
  unavailable roots, cancellation, interrupted-run recovery, forced promotion rollback, and
  multi-file set-based promotion.

## Completed playback and shared-room work

- Added profile-authorized artwork and range-enabled media endpoints, a filterable movie catalog,
  detail pages, compatibility messaging, and revisioned per-profile progress/history.
- Made `playerController.js` the canonical implementation for controls, keyboard input, timeline,
  volume, mute, fullscreen, status, and ARIA synchronization.
- Added controller hooks for playback, completed seeks, buffering, and fullscreen changes so direct
  and shared players compose the same behavior.
- Added one serialized optimistic progress writer with antiforgery headers, ordered revisions,
  conflict handling, and an awaitable final flush. Direct and shared disposal await progress,
  controller cleanup, and shared-hub shutdown.
- Added in-memory shared rooms with pinned versions, ordered commands, participant membership,
  reconnect snapshots, buffering coordination, drift correction, expiry, and idempotent leave.
- Extracted external-process execution from media parsing. Timeout and caller cancellation terminate
  the process tree and await process and redirected-stream completion.

## Completed verification and tooling work

- Added a serial Playwright suite that starts the real application on an isolated loopback port and
  disposable data root.
- The seven browser scenarios cover startup, navigation, valid administrator/profile forms,
  antiforgery rejection, direct progress conflicts, canonical controller hooks, shared-room
  synchronization, buffering, serialized progress ordering, conflict handling, awaited disposal,
  membership cleanup, and identity switching.
- Added pinned Prettier configuration plus authored-source formatting and readability checks.
  Vendored/generated assets, lockfiles, build output, fixtures, and the verbatim root plan are
  excluded.

## Next milestone

The remediation implementation is complete. Future product work should begin as a newly defined
milestone and retain the final acceptance sequence documented in `CONTINUATION_PLAN.md`.
