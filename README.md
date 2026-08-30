# Blockbuster

A self-hosted, cross-platform media server built as a .NET 10 modular monolith with a Blazor Web App front end.

## Development

Prerequisites:

- .NET SDK 10.0.302 or a compatible 10.0 patch release

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet run --project .\Blockbuster
```

The application uses a global Interactive Server render mode. Development defaults
the data root to `Blockbuster/.data`. Other environments must provide an absolute
data root, for example:

```powershell
$env:Storage__DataRoot = 'D:\Blockbuster'
$env:MediaProbe__ExecutablePath = 'C:\Tools\ffmpeg\bin\ffprobe.exe'
$env:Tmdb__Token = '<deployment secret>'
$env:Authentication__BootstrapPin = '<four digits>'
dotnet run --project .\Blockbuster --no-launch-profile
```

Use deployment-local JSON, environment variables, user secrets, or service
configuration for machine paths and secrets. Storage path overrides such as
`Storage__LogsPath` must be absolute; when omitted, generated state remains
beneath `Storage__DataRoot`.

Library movie roots may be absolute paths or paths relative to the application
content root. Relative roots are convenient for a development media directory
kept beside the program, such as `Media/Movies` in
`appsettings.Development.json`.

Logs are written to the console and to daily rolling files under the resolved
logs path (`<data root>/logs` by default). File logs roll at 50 MB and retain the
latest 14 files. Request events intentionally omit query strings.

SQLite is created at `<data root>/database/blockbuster.db` by default. Numbered,
embedded migrations run before the server accepts requests, with WAL mode,
foreign keys, connection pooling, and a five-second busy timeout enabled.
Data-protection keys are persisted at `<data root>/data-protection-keys` so future
authentication cookies can survive restarts.

Operational probes are available at `/health/live` and `/health/ready`. Readiness
reports SQLite integrity, storage writability, ffprobe, media-root availability,
and TMDB configuration; it may therefore return HTTP 503 on an incompletely
configured development machine even while the UI remains usable.

See [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md) for the current milestone and the next bounded step.
See [docs/OPERATIONS.md](docs/OPERATIONS.md) for publishing, Windows Service,
systemd, reverse-proxy, health-check, and local backup guidance.
