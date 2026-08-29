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
- [ ] 03 — Serilog startup, rolling files, and request logging
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

## Next milestone

Milestone 03 should configure Serilog before host construction, integrate it with
`ILogger<T>`, and add console and rolling-file sinks, request logging, context,
retention, noise suppression, sensitive-data safeguards, and clean shutdown
flushing. It should not add SQLite or DbUp yet.
