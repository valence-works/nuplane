# main Development Guidelines

Auto-generated from all feature plans. Last updated: 2026-03-03

## Active Technologies
- C# on .NET 8 (LTS) + `NuGet.Protocol`/NuGet Client SDK, `Microsoft.Extensions.*` hosting/options/logging/health, `System.Diagnostics.Metrics` (002-phase2-feed-governance)
- File-based deterministic store (`state.json`, immutable package folders, active-pointer links), lock file artifacts (`nuplane.lock.json`) (002-phase2-feed-governance)
- C# on .NET 8 (LTS) + `System.Runtime.Loader` (`AssemblyLoadContext`, `AssemblyDependencyResolver`), `Microsoft.Extensions.*` hosting/options/logging/health, `System.Diagnostics.Metrics` (003-phase3-assembly-loading)
- Existing file-based deterministic store (`state.json`, immutable package folders, active-pointer links); loading-specific runtime session state in-memory with diagnostic projection (003-phase3-assembly-loading)
- C# on .NET 8 (LTS) + `Microsoft.Extensions.*` hosting/options/logging/health, `System.Diagnostics.Metrics`, existing NuGet client integration in `Nuplane.NuGet` (004-phase4-operational-enhancements)
- Deterministic file-based store (`state.json`, immutable package folders, active pointers) plus in-memory cycle evaluation state (manifest/source/acquisition/loader/admin outcomes) with persisted diagnostics (004-phase4-operational-enhancements)
- C# 13 / .NET 10 + xUnit 2.9.3, `Microsoft.NET.Test.Sdk`, `coverlet.collector` (all centrally managed via `Directory.Packages.props`) (006-test-backfill)
- N/A for test code; `LockFileCoordinatorTests` uses `Path.GetTempFileName()` for transient JSON lock files (006-test-backfill)
- C# on .NET multi-targeting (`net8.0;net9.0;net10.0`) + `Microsoft.Extensions.*` (DI/Options/Hosting/Logging), xUnit, NSubstitute (004-phase4-operational-enhancements)
- Node-local package/store on filesystem (immutable versioned artifacts + active pointer metadata) (004-phase4-operational-enhancements)

- C# on .NET 8 (LTS) + `NuGet.Protocol`/NuGet Client SDK, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Logging`, `System.Diagnostics.Metrics` (001-phase1-runtime-baseline)

## Project Structure

```text
src/
test/
```

## Commands

# Add commands for C# on .NET 8 (LTS)

## Code Style

C# on .NET 8 (LTS): Follow standard conventions

## Recent Changes
- 004-phase4-operational-enhancements: Added C# on .NET multi-targeting (`net8.0;net9.0;net10.0`) + `Microsoft.Extensions.*` (DI/Options/Hosting/Logging), xUnit, NSubstitute
- 006-test-backfill: Added C# 13 / .NET 10 + xUnit 2.9.3, `Microsoft.NET.Test.Sdk`, `coverlet.collector` (all centrally managed via `Directory.Packages.props`)
- 004-phase4-operational-enhancements: Added C# on .NET 8 (LTS) + `Microsoft.Extensions.*` hosting/options/logging/health, `System.Diagnostics.Metrics`, existing NuGet client integration in `Nuplane.NuGet`


<!-- MANUAL ADDITIONS START -->
<!-- MANUAL ADDITIONS END -->
