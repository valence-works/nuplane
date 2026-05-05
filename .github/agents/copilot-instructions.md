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
- C# on .NET multi-targeting (`net8.0;net9.0;net10.0`) + `Microsoft.Extensions.*` (DI/Options/Hosting/Logging), `System.IO.FileSystemWatcher`, `System.Threading.Channels`, xUnit, NSubstitute (008-local-feeds-and-watchers)
- Node-local filesystem store with transactional activation semantics (stage/validate/publish/atomic switch + LKG fallback) (008-local-feeds-and-watchers)
- C# on .NET multi-targeting (`net8.0;net9.0;net10.0`) + `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Logging`, `Microsoft.Extensions.DependencyInjection`; xUnit for tests (009-startup-and-loading-events)
- N/A — no new store interactions; feature is purely additive to the reconciliation pipeline (009-startup-and-loading-events)
- C# / .NET 8.0, 9.0, 10.0 (multi-target) + Microsoft.Extensions.{Options, Logging, DependencyInjection, Configuration} v10.0.3 (011-version-range-resolution)
- File-system-based package install root (no database) (011-version-range-resolution)
- JSON file persistence via `StoreStateSerializer`; default path under local filesystem (012-default-state-path)
- C# with SDK-style .NET libraries targeting `net8.0;net9.0;net10.0`; tests target `net10.0` + `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Configuration.Binder`, `Microsoft.Extensions.Logging`, xUnit, NSubstitute (013-module-pattern-expansion)
- File-backed package store and state registry managed by `Nuplane.Store`; no new persistence model introduced by this feature (013-module-pattern-expansion)
- C# with SDK-style .NET libraries targeting `net8.0;net9.0;net10.0`; tests target `net10.0` + `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Configuration.Binder`, `Microsoft.Extensions.Logging`, ASP.NET Core Minimal APIs in `Nuplane.Admin.Api`, xUnit, NSubstitute (014-query-package-catalog)
- File-backed package/store state persisted via `IStoreRegistry` at `.nuplane/store-state.json` plus immutable package folders/current pointers; loading read state is current-process projection data owned by the optional loading module (014-query-package-catalog)
- C# with SDK-style .NET libraries targeting `net8.0;net9.0;net10.0`; tests target `net10.0` + `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Logging`, ASP.NET Core Minimal APIs in `Nuplane.Admin.Api` and `Nuplane.Loading.Api`, xUnit, NSubstitute (015-simplify-loading-api)
- File-backed runtime/store state via `IStoreRegistry` and `.nuplane/store-state.json` for durable active inventory; load-state and runtime assembly/type access remain current-process projections over the active se (015-simplify-loading-api)
- Markdown documentation authored in a repository whose product code targets `.NET 8/9/10` + Existing `README.md`, `docs/roadmap.md`, `docs/coding-conventions.md`, `samples/Nuplane.Sample.AspNetCore`, accepted feature specs and quickstarts under `specs/`, GitHub wiki Markdown/linking conventions (016-nuplane-github-wiki)
- Version-controlled Markdown files in the repository (planned under `docs/wiki/`); no runtime data store changes (016-nuplane-github-wiki)
- C# with SDK-style .NET libraries targeting `net8.0;net9.0;net10.0`; tests target `net10.0` + Microsoft.Extensions.DependencyInjection/Options/Logging/Hosting, NuGet.Protocol and NuGet.Versioning already used by feed version resolution, System.Runtime.Loader, xUnit, NSubstitute (017-dependency-closure-loading)
- File-backed Nuplane store state and package install directories under configured state/package roots; no database (017-dependency-closure-loading)

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
- 017-dependency-closure-loading: Added C# with SDK-style .NET libraries targeting `net8.0;net9.0;net10.0`; tests target `net10.0` + Microsoft.Extensions.DependencyInjection/Options/Logging/Hosting, NuGet.Protocol and NuGet.Versioning already used by feed version resolution, System.Runtime.Loader, xUnit, NSubstitute
- 016-nuplane-github-wiki: Added Markdown documentation authored in a repository whose product code targets `.NET 8/9/10` + Existing `README.md`, `docs/roadmap.md`, `docs/coding-conventions.md`, `samples/Nuplane.Sample.AspNetCore`, accepted feature specs and quickstarts under `specs/`, GitHub wiki Markdown/linking conventions
- 015-simplify-loading-api: Added C# with SDK-style .NET libraries targeting `net8.0;net9.0;net10.0`; tests target `net10.0` + `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Logging`, ASP.NET Core Minimal APIs in `Nuplane.Admin.Api` and `Nuplane.Loading.Api`, xUnit, NSubstitute


<!-- MANUAL ADDITIONS START -->
<!-- MANUAL ADDITIONS END -->
