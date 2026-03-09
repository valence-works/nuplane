# Data Model — Module Pattern Expansion

## Entity: ModulePackageSet
- Purpose: Defines the package ownership boundary for one optional or source-specific Nuplane capability.
- Fields:
  - `moduleName` (string, required, unique)
  - `implementationProject` (string, required)
  - `builderIntegrationProject` (string, optional)
  - `directRegistrationSurface` (string, required)
  - `builderConvenienceSurfaces` (list<string>, optional)
  - `ownedOptionsTypes` (list<string>, required)
  - `ownedHostedServices` (list<string>, required)
  - `ownedRegistrationServices` (list<string>, required)
  - `ownedTestProjects` (list<string>, required)
- Validation rules:
  - `implementationProject` must own module-specific options, hosted services, and registration helpers.
  - `builderIntegrationProject`, when present, owns fluent APIs only and delegates to module registration services.
  - Core `Nuplane` cannot be listed as the owner of module-specific hosted services or module options.

## Entity: ModuleRegistrationSurface
- Purpose: Defines a supported public entrypoint for enabling a module.
- Fields:
  - `moduleName` (string, required)
  - `surfaceKind` (enum: `direct-service-collection`, `builder-integration`)
  - `owningProject` (string, required)
  - `methodName` (string, required)
  - `optionsType` (string, optional)
  - `corePrerequisite` (string, required)
  - `delegateTarget` (string, required)
- Validation rules:
  - Every module requires at least one `direct-service-collection` surface.
  - Builder-integration surfaces must delegate to a shared module registration target.
  - Public documentation must state that core registration is a prerequisite, not a hidden module-enablement path.

## Entity: DuplicateRegistrationPolicy
- Purpose: Captures deterministic behavior when the same module is registered through multiple supported paths.
- Fields:
  - `moduleName` (string, required)
  - `precedenceRule` (enum: `last-registration-wins`)
  - `replacedState` (list<string>, required; e.g. `options`, `hosted-services`, `observer-routing`)
  - `deduplicatedServiceKeys` (list<string>, required)
  - `documentationRequired` (bool, required = true)
- Validation rules:
  - The later registration must become authoritative for the module.
  - The resulting service graph cannot include duplicate hosted services, event dispatchers, or conflicting options consumers.
  - Builder and direct registration paths must share the same replacement semantics.

## Entity: CompatibilityWrapperRetirement
- Purpose: Tracks temporary wrapper APIs that exist only until module-owned replacements are available.
- Fields:
  - `wrapperName` (string, required)
  - `owningProject` (string, required)
  - `replacementSurface` (string, required)
  - `retirementPhase` (enum: `feature-complete`)
  - `migrationNoteLocation` (string, required)
- Validation rules:
  - Temporary wrappers must delegate to module-owned registration services only.
  - Temporary wrappers must be removed by the end of this feature.
  - Documentation must identify the replacement surface before wrapper removal.

## Entity: ModuleObservabilityBinding
- Purpose: Captures the logging, health, and event signals that must remain intact when ownership moves between packages.
- Fields:
  - `moduleName` (string, required)
  - `loggerTypes` (list<string>, required)
  - `healthSignals` (list<string>, required)
  - `eventDispatchers` (list<string>, optional)
  - `degradationTrackers` (list<string>, optional)
  - `regressionTests` (list<string>, required)
- Validation rules:
  - Any moved hosted service must preserve equivalent structured logs and health signaling.
  - If a module emits observer events, the dispatcher registration must remain singleton-safe.

## Relationships
- `ModulePackageSet` owns one or more `ModuleRegistrationSurface` records.
- `ModulePackageSet` is governed by exactly one `DuplicateRegistrationPolicy`.
- `CompatibilityWrapperRetirement` points to a replacement `ModuleRegistrationSurface`.
- `ModuleObservabilityBinding` attaches to one `ModulePackageSet` and is verified by module-scoped tests.

## State Transitions

### Module boundary migration lifecycle
1. `CoreWrapperOrMixedOwnership`
2. `DirectModuleRegistrationAvailable`
3. `BuilderIntegrationPackageAvailable`
4. `CompatibilityWrapperDeprecatedOrDelegating`
5. `CoreWrapperRemoved`
6. `DocumentationAndTestsAligned`

### Registration resolution lifecycle
1. `CoreRegistered`
2. `ModuleRegistrationRequested`
3. `SharedModuleRegistrationApplied`
4. `PreviousModuleStateReplacedIfPresent`
5. `ServiceGraphDeduplicated`
6. `OptionsValidatedOnStart`
7. `RuntimeReady`