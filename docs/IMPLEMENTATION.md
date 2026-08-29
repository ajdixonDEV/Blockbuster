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
- [ ] 04 — SQLite connection factory and DbUp migration runner
- [ ] 05 — Data-protection persistence and health checks
- [ ] 06 — Windows Service/systemd hosting and operator documentation
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

Milestone 04 should add the pooled SQLite connection factory and DbUp migration
runner, including foreign keys, WAL mode, busy timeout, embedded numbered scripts,
one transaction per migration, startup failure behavior, and migration tests. It
should not add profiles or application repositories yet.

## UI skeleton

- Added Blazor Blueprint Components 3.15.0 and Lucide Icons 2.0.2 as the UI
  foundation, including services, precompiled styles, theme variables, imports,
  and the required root portal host.
- Added a responsive retro rental-store shell, home hero, Blueprint library
  cards, and routed catalog empty states for Movies, TV, Videos, and Music.
- Added a Blueprint movie toolbar skeleton. Controls that depend on future
  persistence and profile work remain disabled rather than implying behavior.
