# Repository Guidelines

## Project Structure & Module Organization

`SnapBoard.slnx` groups production code under `src/` and verification projects under `tests/`. `SnapBoard.Domain` contains framework-free values and rules. `SnapBoard.Application` defines use cases and ports and may depend only on Domain, `Platform.Abstractions`, and `Sync.Contracts`. `SnapBoard.Infrastructure` implements SQLite, file, configuration, and encryption concerns. Operating-system code belongs in the matching `SnapBoard.Platform.*` project. `SnapBoard.Sync.Contracts` owns versioned DTOs and the `System.Text.Json` source-generation context; `SnapBoard.Sync.WebDav` owns transport behavior. `SnapBoard.Desktop` is the Avalonia UI and the only dependency-composition root.

Architecture decisions and current work status are recorded in `PLAN.md`, `docs/PROGRESS.md`, and `docs/adr/`. Update those files when a change alters an accepted boundary or completes a milestone.

## Build, Test, and Development Commands

The repository requires the SDK pinned by `global.json`.

```bash
dotnet restore SnapBoard.slnx --locked-mode
dotnet build SnapBoard.slnx --configuration Release --no-restore
dotnet test SnapBoard.slnx --configuration Release --no-build --no-restore
dotnet test tests/SnapBoard.Domain.Tests/SnapBoard.Domain.Tests.csproj --configuration Release
dotnet run --project src/SnapBoard.Desktop/SnapBoard.Desktop.csproj
```

Native AOT must be published on the target operating system. For example, use `dotnet publish src/SnapBoard.Desktop/SnapBoard.Desktop.csproj -c Release -r osx-arm64 --self-contained true -p:PublishAot=true` on Apple Silicon.

## Coding Style & Naming Conventions

`.editorconfig` enforces UTF-8, LF, four-space C# indentation, file-scoped namespaces, sorted `System` directives, and removal of unused imports. `Directory.Build.props` enables nullable references, C# 14, recommended .NET analyzers, deterministic builds, and warnings as errors. Keep important platform, concurrency, encryption, persistence, and protocol comments in Chinese; omit narration for self-explanatory members.

Do not introduce assembly scanning, runtime-reflection serialization, Newtonsoft.Json, or ORM types into the core layers. SQL must remain parameterized and isolated in Infrastructure. Sync DTOs must be registered in `SyncJsonContext`.

## Testing Guidelines

xUnit projects mirror the production boundaries. Add pure rule tests to Domain/Application, SQLite integration tests to Infrastructure, WebDAV contract tests to Sync, and dependency-direction assertions to Architecture.Tests. `SnapBoard.PerformanceTests` uses BenchmarkDotNet and is run with `dotnet run`, not `dotnet test`. Every release path must retain zero unexplained trim or Native AOT warnings.
