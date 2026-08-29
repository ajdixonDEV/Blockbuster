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
- [ ] 07 — Profiles and administrator foundation
- [ ] 08 — Shared media primitives and movie scanning
- [ ] 09 — Movie catalog and direct playback
- [ ] 10 — Shared movie rooms

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

## Next milestone

Milestone 07 should implement the migration-backed administrator credential and
profiles, bootstrap and local reset behavior, independent signed session cookies,
profile selection guards, and focused browser administration for profile CRUD.
It should also surface the existing health/configuration summaries behind the
administrator boundary.

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
