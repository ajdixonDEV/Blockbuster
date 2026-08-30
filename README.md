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

<img width="2541" height="1307" alt="image" src="https://github.com/user-attachments/assets/49e035f0-bf13-4275-8599-210b2d7c8c92" />
<img width="2551" height="1304" alt="image" src="https://github.com/user-attachments/assets/7092bedf-e9ec-4207-a468-5533e9f35956" />
<img width="2552" height="1305" alt="image" src="https://github.com/user-attachments/assets/8fe788f9-3050-47ca-958d-17ef2e32dfd0" />
<img width="2533" height="1307" alt="image" src="https://github.com/user-attachments/assets/ec0531e6-bd28-400f-a58b-e28cf98248c3" />
<img width="2534" height="1298" alt="image" src="https://github.com/user-attachments/assets/fc45659c-bfcb-4c42-b5d8-661ff90e6fc1" />




