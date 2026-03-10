# Phase 0 Research — Module Pattern Expansion

## Decision 1: Use implementation-plus-hosting module ownership as the standard pattern
- Decision: Standardize optional modules on an implementation package that owns options, validators, hosted services, and direct registration services, plus an optional hosting/builder integration package that owns fluent APIs.
- Rationale: This matches the strongest existing pattern in the repo: loading already has a dedicated hosting package, while directory-source already owns its hosted service and registration helpers. Making the split explicit removes module logic from core without forcing every implementation package to depend on builder fluency concerns.
- Alternatives considered: Keep module-specific fluent APIs in `Nuplane` permanently (rejected: preserves architectural drift); keep everything in one module package (rejected: couples runtime implementation to builder ergonomics and diverges from loading).

## Decision 2: Treat directory-source as the baseline migration and add a dedicated builder integration package
- Decision: Keep `DirectorySourceOptions`, `DirectorySourceRegistrationServices`, and `DirectorySourceReconciliationTriggerHostedService` in `Nuplane.Sources.Directory`, and add a new `Nuplane.Sources.Directory.Hosting` package for builder-facing directory conveniences currently exposed through core feed builder APIs.
- Rationale: The directory module already owns its options and hosted service, so the missing piece is fluent API ownership. A dedicated hosting/builder package aligns directory-source with the chosen long-term model and allows removal of the core wrapper by feature end.
- Alternatives considered: Leave `NuplaneFeedBuilder.FromDirectory(...)` in core as a permanent delegated wrapper (rejected: contradicts the clarified ownership rule); move hosted-service logic into the hosting package (rejected: weakens the implementation package’s ownership of module behavior).

## Decision 3: Finish loading by adding a direct module registration surface in the implementation package
- Decision: Add a module-owned direct registration surface for loading in `Nuplane.Loading`, backed by reusable registration services that the existing `Nuplane.Loading.Hosting` builder extensions delegate into.
- Rationale: Loading already has a builder integration package, but it still lacks a direct `IServiceCollection` registration path owned by the module implementation. Adding that surface satisfies the module contract and makes builder and direct registration share one implementation path.
- Alternatives considered: Leave loading builder-only (rejected: fails FR-003); put direct registration in `Nuplane` (rejected: keeps module-specific ownership in core).

## Decision 4: Normalize loading options ownership into the loading module, not abstractions
- Decision: Keep loading contracts in `Nuplane.Loading.Abstractions`, but move `LoadingOptions` and `LoadingOptionsValidator` ownership to the loading module implementation path so registration and validation live with the module that consumes them.
- Rationale: `DirectorySourceOptions` already lives in the module package that consumes it. Applying the same rule to loading avoids abstractions packages accumulating implementation-only configuration and keeps `IValidateOptions<T>` registration close to the services that require it.
- Alternatives considered: Leave `LoadingOptions` in abstractions (rejected: abstractions should remain minimal and implementation-agnostic); move options into hosting only (rejected: direct registration would still depend on builder-specific assembly structure).

## Decision 5: Define duplicate registration as last-registration-wins through shared module registration services
- Decision: Builder and direct registration paths must converge on shared module registration services that enforce last-registration-wins semantics, replace earlier module registration state deterministically, and avoid duplicate hosted services, observers, event dispatchers, or conflicting options consumers.
- Rationale: The spec clarification requires the latest registration to become authoritative. Centralizing the behavior in module registration services keeps the service graph deterministic and prevents the builder surface from implementing its own divergent override logic.
- Alternatives considered: First-registration-wins (rejected by clarification); throw on duplicate registration (rejected by clarification); allow duplicate additive registration (rejected: risks duplicate hosted services and conflicting observers).

## Decision 6: Preserve observability and safety by keeping registration helpers delegation-only
- Decision: Module registration helpers may bind options, register validators, and install hosted/observer services, but they must not alter reconciliation, transaction, or trust semantics beyond wiring existing module-owned components.
- Rationale: Existing directory degradation tracking, store LKG behavior, source-trust enforcement, and loading observer dispatch already satisfy constitutional requirements. The feature should move ownership, not rewrite the safety-critical runtime/store behaviors underneath it.
- Alternatives considered: Fold runtime/store behavior into module-specific wrappers (rejected: violates transactional and host-neutral boundaries); postpone observability preservation until later phases (rejected: fails OSR-004).

## Decision 7: Backfill contract tests around registration determinism and wrapper removal
- Decision: Add focused tests for directory and loading duplicate registration, hosted-service deduplication, direct registration availability, builder delegation, and removal of superseded core wrappers while retaining existing runtime/store integration tests.
- Rationale: Current tests prove optionality, debounce behavior, loading lifecycle, and LKG safety, but they do not yet cover the new “last registration wins” module contract. Targeted contract tests close the highest-risk gap introduced by this refactor.
- Alternatives considered: Rely on existing unit and integration tests only (rejected: they do not assert the new boundary contract); postpone test backfill to a later feature (rejected: conflicts with constitution test discipline).