# Implementation Plan: Module Pattern Expansion

**Branch**: `[013-module-pattern-expansion]` | **Date**: 2026-03-09 | **Spec**: `/Users/sipke/Projects/ValenceWorks/nuplane/main/specs/013-module-pattern-expansion/spec.md`
**Input**: Feature specification from `/Users/sipke/Projects/ValenceWorks/nuplane/main/specs/013-module-pattern-expansion/spec.md`

## Summary

Normalize optional-module ownership across Nuplane so that directory-source and loading capabilities expose module-owned direct registration surfaces, keep module-specific options and hosted services out of core, and move module-specific fluent APIs into module-owned builder integration packages. The implementation should treat `Nuplane.Sources.Directory` as the baseline pattern, finish the missing loading direct-registration path, add a dedicated directory builder integration package, remove superseded core wrappers, and add deterministic duplicate-registration coverage with last-registration-wins semantics.

## Technical Context

**Language/Version**: C# with SDK-style .NET libraries targeting `net8.0;net9.0;net10.0`; tests target `net10.0`  
**Primary Dependencies**: `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Configuration.Binder`, `Microsoft.Extensions.Logging`, xUnit, NSubstitute  
**Storage**: File-backed package store and state registry managed by `Nuplane.Store`; no new persistence model introduced by this feature  
**Testing**: `dotnet test` with xUnit-based unit, contract, and integration suites under `test/`  
**Target Platform**: Cross-platform .NET host applications on macOS, Linux, and Windows  
**Project Type**: Multi-project .NET library solution with optional module/hosting integration packages  
**Performance Goals**: Preserve current debounce/coalescing guarantees for directory observation and ensure duplicate registration does not create extra hosted services, observers, or event dispatchers  
**Constraints**: Preserve deterministic reconciliation and LKG safety; keep core host-neutral; keep source-trust boundaries intact; validate options through `IValidateOptions<T>` plus `ValidateOnStart()`; remove superseded core module wrappers by the end of the feature  
**Scale/Scope**: Affects `src/Nuplane`, `src/Nuplane.Sources.Directory`, `src/Nuplane.Loading`, `src/Nuplane.Loading.Hosting`, planned `src/Nuplane.Sources.Directory.Hosting`, and related runtime/loading/directory/integration/store test projects

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Initial Gate Assessment

- Deterministic reconciliation: PASS. The plan keeps debounce/coalescing logic in the directory module, routes duplicate registration through shared module registration services, and defines last-registration-wins semantics without changing reconciliation engine retry behavior.
- Transactional store safety: PASS. Registration-surface refactors stay above store transaction boundaries and do not alter stage/validate/publish/atomic-switch or LKG fallback behavior in `Nuplane.Store`.
- Source integrity: PASS. Directory registration continues to register trusted local feeds explicitly, keeps credentials out of source-controlled defaults, and leaves trust validation in module-owned registration helpers.
- Observability: PASS. Directory watcher degradation tracking, loading event dispatch, structured logs, and existing health projection hooks remain required outputs of the module-owned services after the move.
- Test discipline: PASS. The design requires new unit/contract coverage for direct registration, duplicate registration determinism, hosted-service deduplication, and wrapper removal, while preserving existing integration/store regression tests.
- Decomposition discipline: PASS. Mechanism and driver are separated into direct registration services, builder integration packages, compatibility-wrapper removal, and dedicated tests; each planned task can map to a single artifact or tightly coupled file group.
- Options validation discipline: PASS. Options remain data-only, loading and directory validators stay in the .NET options pipeline, and any moved registration path must continue to call `ValidateOnStart()`.

### Post-Design Gate Assessment

- Deterministic reconciliation: PASS. `research.md` requires module registration services to own replacement semantics so builder and direct APIs converge on one deterministic service graph.
- Transactional store safety: PASS. `contracts/module-registration-contract.md` keeps `AddNuplane(...)` as the core prerequisite and forbids module registration helpers from mutating store lifecycle behavior.
- Source integrity: PASS. `data-model.md` and `research.md` keep trust-level and allowlist handling inside module-owned registration contracts, not in core compatibility wrappers.
- Observability: PASS. `data-model.md` includes module observability bindings and `quickstart.md` verifies directory degradation and loading observer behavior after the refactor.
- Test discipline: PASS. `quickstart.md` names the required runtime, loading, directory, integration, and store verification scope, including new determinism tests.
- Decomposition discipline: PASS. The design splits loading implementation ownership, directory builder-package extraction, wrapper removal, and test backfill into separate implementation tracks.
- Options validation discipline: PASS. The design keeps `LoadingOptions` and `DirectorySourceOptions` as data objects and routes constraints through validators registered from module-owned services.

## Project Structure

### Documentation (this feature)

```text
specs/013-module-pattern-expansion/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── builder-integration-contract.md
│   └── module-registration-contract.md
└── tasks.md
```

### Source Code (repository root)

```text
src/
├── Nuplane/
│   ├── Builder/
│   ├── Feeds/
│   ├── Registration/
│   └── Setup/
├── Nuplane.Abstractions/
├── Nuplane.Loading/
├── Nuplane.Loading.Abstractions/
├── Nuplane.Loading.Hosting/
├── Nuplane.NuGet/
├── Nuplane.Runtime/
├── Nuplane.Sources.Directory/
├── Nuplane.Sources.Directory.Hosting/   # planned by this feature
└── Nuplane.Store/

test/
├── Nuplane.Integration.Tests/
├── Nuplane.Loading.Tests/
├── Nuplane.NuGet.Tests/
├── Nuplane.Runtime.Tests/
├── Nuplane.Sources.Directory.Tests/
└── Nuplane.Store.Tests/
```

**Structure Decision**: Keep the existing multi-project library layout. Implementation packages own module options, hosted services, registration services, and tests; hosting/builder integration packages own fluent APIs; `Nuplane` retains only core composition and non-module infrastructure. This feature adds `src/Nuplane.Sources.Directory.Hosting/` so directory-source follows the same implementation-plus-hosting split already established by loading.

## Complexity Tracking

No constitution violations or extra complexity justifications are required at planning time.
