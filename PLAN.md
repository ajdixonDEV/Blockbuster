# Blockbuster Code-Quality Remediation Plan

## Summary

Create `PLAN.md` at the repository root containing this remediation program. Address the audit findings in dependency order while preserving current behavior, database compatibility, and the user’s uncommitted work.

All hand-authored source must be reformatted so its physical structure honestly reflects its complexity. Vendored SignalR bundles, source maps, lockfiles, binaries, and generated/build output remain untouched.

## Implementation Changes

### 1. Restore trustworthy validation and endpoint security

- Replace PID-derived Playwright settings with a Node test launcher that selects one free loopback port and unique data root, exports both through environment variables, and then launches Playwright. The config must consume only those stable values.
- Split the web composition root into authentication, administration, playback, shared-room, and health endpoint modules.
- Apply administrator authorization through route groups instead of repeating `AuthenticateAsync("Admin")`.
- Configure antiforgery with an `X-CSRF-TOKEN` header, render request tokens for JavaScript, add antiforgery tokens to every POST form, and remove the blanket `DisableAntiforgery` calls.
- Remove the unreferenced JSON `/api/shared` endpoint; retain the form-based shared-room creation flow.

### 2. Canonicalize playback and external-process lifecycle

- Make `playerController.js` the only implementation of play/pause, timeline, volume, mute, fullscreen, keyboard, status, and ARIA synchronization.
- Extend it with explicit hooks for seek completion, buffering, and playback events so shared playback composes it instead of replacing handlers.
- Extract one serialized optimistic progress writer used by direct and shared playback. It must preserve revision ordering, handle conflicts consistently, attach the antiforgery header, and expose an awaitable final flush.
- Make both player disposal paths asynchronous and await the final progress save, controller cleanup, and SignalR shutdown.
- Extract process execution lifecycle from ffprobe parsing. On timeout or caller cancellation, terminate the entire process tree, await exit and redirected-stream completion, then return the correct timeout or cancellation exception.

### 3. Simplify scanning and catalog persistence

- Replace the broad `IMovieCatalogStore` with a match-focused store contract; keep scan-run and observation persistence internal to Infrastructure.
- Remove unused direct-scanning APIs and models, including direct file upsert/missing methods, automatic-resolution entry points, unused promotion payloads, unused observation fields, and dead helpers.
- Introduce one transaction-aware catalog transition writer for pending-match and metadata association updates. Manual resolution and staged promotion must call the same implementation.
- Stage observations in one explicit transaction with a prepared command.
- Promote media facts set-wise from the staging table rather than issuing a lookup and upsert for every file. Only changed metadata-resolution payloads may require per-item processing.
- Combine failed-run completion and staging cleanup into one atomic store operation. Preserve existing rollback, availability, association, and recovery behavior.

### 4. Remove inert contracts and documentation drift

- Remove `RequireMatchingYear`, `RequireUniqueMatch`, and `PreferBrowserCompatibleVersions` from option types, configuration, validation, documentation, and tests; current strict matching and explicit version selection remain unchanged.
- Remove empty/dead components and helpers.
- Update implementation documentation so completed and next milestones are ordered and accurate.
- Preserve all SQLite migrations and existing on-disk data formats.

### 5. Undo one-line compression across the solution

- Reformat every hand-authored C#, Razor, JavaScript, CSS, SQL, and test file—not only files otherwise changed by this refactor.
- Expand multi-statement lines, compressed classes/records, chained conditionals, event handlers, Razor branches/tags, SQL clauses, and CSS rules. Use one declaration, branch, statement, property, or CSS declaration per logical line.
- Keep expression-bodied members only when they contain one trivial operation and remain immediately readable.
- Run `dotnet format` over the solution and require `dotnet format --verify-no-changes` to pass.
- Add pinned Prettier tooling for authored JavaScript, CSS, JSON, and Markdown with `format` and `format:check` scripts.
- Add a repository readability check for authored `.cs`, `.razor`, `.js`, `.css`, and `.sql` files that rejects lines over 160 characters and excludes only the agreed vendored/generated paths.
- Add root formatting configuration and reflow long SQL/raw strings rather than exempting individual project files.

## Public Interface Changes

- Remove the unused direct scan-mutation methods and their data types from Core.
- Rename/narrow the catalog mutation contract to describe movie-match transitions only.
- Move scan promotion details behind an Infrastructure-owned persistence boundary.
- Remove the unused `/api/shared` HTTP endpoint.
- Add no database migration or externally visible replacement API.

## Test and Acceptance Plan

- Add endpoint tests proving mutation requests without antiforgery tokens fail and valid profile/admin requests succeed.
- Add shared-player tests for keyboard behavior, ARIA state, fullscreen entry/exit, seek synchronization, buffering, serialized progress saves, conflicts, and awaited disposal.
- Add process-runner tests using a deterministic long-running child process to prove timeout and caller cancellation both terminate the child.
- Retain and update scanning tests for successful promotion, unchanged facts, missing files, cancellation cleanup, interrupted recovery, and forced rollback; add a multi-file promotion case exercising the set-based path.
- Run and require:

  - `dotnet format Blockbuster.slnx --no-restore --verify-no-changes`
  - `dotnet build Blockbuster.slnx --no-restore`
  - `dotnet test Blockbuster.slnx --no-build`
  - `npm run format:check`
  - the authored-source readability check
  - `npm run test:browser`, with all seven browser tests passing

## Assumptions

- The current working tree is the implementation baseline and unrelated user changes must be preserved.
- “Everywhere” means all hand-authored project source and tests. Vendored SignalR assets, maps, package locks, media fixtures, binaries, and generated output are intentionally excluded.
- The target artifact is `C:\Users\adixo\source\repos\Blockbuster\PLAN.md`.
- This planning turn does not create the file; execution mode will write this plan verbatim before implementation begins.
