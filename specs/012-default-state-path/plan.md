# Implementation Plan: Default State Path

**Branch**: `012-default-state-path` | **Date**: 2026-03-08 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/012-default-state-path/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/plan-template.md` for the execution workflow.

## Summary

Nuplane currently treats an omitted `StateFilePath` as an implicit in-memory store. That makes persistence optional by accident and silently drops reconciliation state across restarts. This feature changes the configuration pipeline to resolve a single effective persistence mode: use the configured path, otherwise default to `.nuplane/store-state.json` under `AppContext.BaseDirectory`, unless the operator explicitly opts into `UseInMemoryStore=true`. The design keeps low-level `StoreRegistry(string? stateFilePath)` test semantics intact, introduces centralized effective-settings resolution and `IValidateOptions<T>` validation, keeps store-state loading lazy on first store activation rather than eagerly blocking host startup, adds structured first-activation logging for the resolved mode/path, and preserves transactional safety by failing reconciliation if a persisted-state write fails.

## Technical Context

**Language/Version**: C# / .NET 8.0, 9.0, 10.0 (multi-target)  
**Primary Dependencies**: Microsoft.Extensions.{Options, Logging, DependencyInjection, Configuration} v10.0.3  
**Storage**: JSON file persistence via `StoreStateSerializer`; default path under local filesystem  
**Testing**: xUnit 2.9.3, NSubstitute 5.3.0, `dotnet test`  
**Target Platform**: Cross-platform .NET host integrations (ASP.NET Core and similar self-hosted apps)
**Project Type**: Library/runtime infrastructure  
**Performance Goals**: Constant-time effective-path resolution at startup; no additional per-cycle I/O beyond existing store reads/writes; unchanged restart-load performance aside from using the default path when configured implicitly  
**Constraints**: Preserve host-neutral runtime boundaries; use .NET options validation pipeline with `ValidateOnStart()`; keep transactional store semantics and LKG behavior intact; maintain existing direct-constructor test ergonomics where explicit null still means in-memory for manually constructed registries  
**Scale/Scope**: One persistence configuration per host; affects setup binding, builder translation, store runtime, startup logging, and regression/integration tests across runtime/store packages

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- **Deterministic reconciliation**: ✅ PASS — Effective state mode/path is resolved once from stable startup inputs. Repeated starts with the same config and host root produce the same persistence mode and path. No new retry loops are introduced.
- **Transactional store safety**: ✅ PASS — State persistence remains inside `StoreRegistry` save points. Persisted-mode write failure continues to propagate as an operation failure rather than silently downgrading to ephemeral state, preserving LKG correctness.
- **Source integrity**: ✅ PASS — Feature is local filesystem only. No new external sources, credentials, or trust boundaries are introduced.
- **Observability**: ✅ PASS — Plan includes structured first-activation logs for effective persistence mode/path and explicit failure logs for persistence errors.
- **Test discipline**: ✅ PASS — Unit, configuration-boundary, and integration restart tests are defined, including regression coverage for the missing-path bug.
- **Decomposition discipline**: ✅ PASS — Requirements map to concrete elements: setup options, store options, validators, builder methods, runtime resolver/settings, store registry, and config/boundary tests. `UseInMemoryStore` has explicit consumer and validator tasks.
- **Options validation discipline**: ✅ PASS — Options stay data-only. Validation is implemented via `IValidateOptions<NuplaneSetupOptions>` and `IValidateOptions<StoreRegistryOptions>`, with `ValidateOnStart()` added for store options.

## Project Structure

### Documentation (this feature)

```text
specs/012-default-state-path/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── store-persistence-configuration.md
└── tasks.md
```

### Source Code (repository root)

```text
src/
├── Nuplane/
│   ├── Builder/
│   │   └── NuplaneBuilder.cs                     # add explicit in-memory builder API
│   ├── NuplaneServiceCollectionExtensions.cs    # validator registration, ValidateOnStart, setup translation
│   └── Setup/
│       ├── NuplaneSetupOptions.cs               # add UseInMemoryStore and updated docs
│       └── NuplaneSetupOptionsValidator.cs      # reject blank path + in-memory/path conflicts
└── Nuplane.Store/
    └── State/
        ├── StoreRegistry.cs                     # lazily consume effective settings and log resolved mode/path on first activation
        ├── StoreRegistryOptions.cs              # add UseInMemoryStore and updated docs
        ├── StoreRegistryOptionsValidator.cs     # NEW: runtime/store-level options validation
        └── EffectiveStorePersistenceSettings.cs # NEW: resolved mode/path model (or equivalent helper)

test/
├── Nuplane.Runtime.Tests/
│   └── Configuration/
│       ├── ConfigurationDrivenRegistrationTests.cs  # setup-to-store translation precedence and defaults
│       └── NuplaneSetupOptionsValidatorTests.cs     # NEW/extended setup validator coverage
├── Nuplane.Store.Tests/
│   └── State/
│       ├── StoreRegistryOptionsValidatorTests.cs    # NEW runtime validator tests
│       └── StoreRegistryTests.cs                    # NEW effective path + explicit in-memory behavior tests
└── Nuplane.Integration.Tests/
    └── Reconciliation/
        └── StartupLoadingEventIntegrationTests.cs   # restart-load/default-path regression tests
```

**Structure Decision**: Keep all changes inside existing `Nuplane`, `Nuplane.Store`, and test projects. No new project is required. A small resolved-settings model in `Nuplane.Store.State` centralizes path defaulting and avoids duplicating interpretation logic between setup translation, store runtime, and tests.

## Post-Design Constitution Re-evaluation

- **Deterministic reconciliation**: ✅ PASS — Chosen design resolves effective settings once and exposes a single normalized path to runtime consumers.
- **Transactional store safety**: ✅ PASS — `StoreRegistry` remains the only writer; persisted-mode save exceptions continue to fail the operation.
- **Source integrity**: ✅ PASS — Still local-only filesystem behavior.
- **Observability**: ✅ PASS — Logging occurs when effective store settings are first activated, which guarantees a single authoritative description of the chosen mode without requiring eager startup loading.
- **Test discipline**: ✅ PASS — Plan includes regression tests for default path behavior, explicit in-memory opt-out, configuration precedence, and restart reload semantics.
- **Decomposition discipline**: ✅ PASS — Config shape, validation, runtime resolution, builder API, and tests are separated into artifact-level tasks.
- **Options validation discipline**: ✅ PASS — Store options now participate in fail-fast startup validation alongside setup options.

## Complexity Tracking

No constitution violations to justify.
