# main Development Guidelines

Auto-generated from all feature plans. Last updated: 2026-03-03

## Active Technologies
- C# on .NET 8 (LTS) + `NuGet.Protocol`/NuGet Client SDK, `Microsoft.Extensions.*` hosting/options/logging/health, `System.Diagnostics.Metrics` (002-phase2-feed-governance)
- File-based deterministic store (`state.json`, immutable package folders, active-pointer links), lock file artifacts (`nuplane.lock.json`) (002-phase2-feed-governance)
- C# on .NET 8 (LTS) + `System.Runtime.Loader` (`AssemblyLoadContext`, `AssemblyDependencyResolver`), `Microsoft.Extensions.*` hosting/options/logging/health, `System.Diagnostics.Metrics` (003-phase3-assembly-loading)
- Existing file-based deterministic store (`state.json`, immutable package folders, active-pointer links); loading-specific runtime session state in-memory with diagnostic projection (003-phase3-assembly-loading)
- C# on .NET 8 (LTS) + `Microsoft.Extensions.*` hosting/options/logging/health, `System.Diagnostics.Metrics`, existing NuGet client integration in `Nuplane.NuGet` (004-phase4-operational-enhancements)
- Deterministic file-based store (`state.json`, immutable package folders, active pointers) plus in-memory cycle evaluation state (manifest/source/acquisition/loader/admin outcomes) with persisted diagnostics (004-phase4-operational-enhancements)

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
- 004-phase4-operational-enhancements: Added C# on .NET 8 (LTS) + `Microsoft.Extensions.*` hosting/options/logging/health, `System.Diagnostics.Metrics`, existing NuGet client integration in `Nuplane.NuGet`
- 003-phase3-assembly-loading: Added C# on .NET 8 (LTS) + `System.Runtime.Loader` (`AssemblyLoadContext`, `AssemblyDependencyResolver`), `Microsoft.Extensions.*` hosting/options/logging/health, `System.Diagnostics.Metrics`
- 002-phase2-feed-governance: Added C# on .NET 8 (LTS) + `NuGet.Protocol`/NuGet Client SDK, `Microsoft.Extensions.*` hosting/options/logging/health, `System.Diagnostics.Metrics`


<!-- MANUAL ADDITIONS START -->
<!-- MANUAL ADDITIONS END -->
