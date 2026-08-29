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
configuration for machine paths and secrets. Do not commit them. Storage path
overrides such as `Storage__LogsPath` must be absolute; when omitted, generated
state remains beneath `Storage__DataRoot`.

See [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md) for the current milestone and the next bounded step.
