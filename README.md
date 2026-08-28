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

The application uses a global Interactive Server render mode. Machine-specific paths and secrets must not be committed; later milestones will define their configuration sources.

See [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md) for the current milestone and the next bounded step.
