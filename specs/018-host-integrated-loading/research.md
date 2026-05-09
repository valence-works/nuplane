# Research: Host-Integrated Package Loading

## Decision: Keep collectible as the default load mode

**Rationale**: Existing consumers currently expect loaded package assemblies to come from collectible package contexts. Preserving that default avoids silent lifetime and isolation changes, keeps backward compatibility, and requires hosts to opt into the broader visibility and longer lifetime of host-integrated loading.

**Alternatives considered**:
- Make host-integrated the default when loading is enabled: rejected because it changes current unloadability/isolation semantics.
- Auto-detect framework-integrated packages: rejected because framework participation is ambiguous and could surprise hosts.

## Decision: Add an explicit `PackageLoadMode` model with `Collectible` and `HostIntegrated`

**Rationale**: Shared assembly policy, package lifetime, and resolution visibility are independent decisions. A dedicated load mode makes lifetime and resolution behavior explicit without overloading shared assembly configuration.

**Alternatives considered**:
- Reuse shared assembly policy to imply host integration: rejected because shared assemblies solve contract identity only.
- Add boolean flags such as `UseDefaultContext`: rejected because load behavior is a closed set that should validate deterministically and leave room for future modes.

## Decision: Extend the existing package assembly catalog with load-mode and framework-safety metadata

**Rationale**: The current package assembly catalog is already the host-facing discovery surface for active loaded assemblies. Extending its returned models with load mode and framework-safety metadata keeps discovery cohesive while making host-integrated safety explicit.

**Alternatives considered**:
- Add a separate host-integrated catalog: rejected because consumers would need to choose between overlapping discovery surfaces.
- Keep the catalog unchanged and add only a resolver service: rejected because callers also need to know whether returned assemblies are safe for framework integration.

## Decision: Reject host-integrated activation on conflicting assembly simple names with different versions

**Rationale**: Framework assembly resolution by simple name must not bind arbitrarily. Failing activation before the package graph becomes visible preserves deterministic reconciliation and avoids hidden runtime binding drift.

**Alternatives considered**:
- Defer conflict failure until resolution is requested: rejected because activation could appear healthy while later framework operations fail.
- Prefer highest version: rejected because it can mask incompatibilities and violate package graph intent.
- Prefer last-known-good version: rejected for initial activation conflicts because it can hide the new desired state failure.

## Decision: Apply last-known-good fallback to host-integrated replacement visibility

**Rationale**: Nuplane already preserves last-known-good state for failed updates. Host-integrated assembly visibility must follow the same safety rule: replacement mappings become visible only after successful activation and visibility setup; otherwise the previous visible mapping remains active.

**Alternatives considered**:
- Require process restart for every replacement: rejected because it blocks Nuplane's runtime update value for compatible host-integrated replacements.
- Switch visibility immediately after package install: rejected because framework code could observe a package before load and conflict checks complete.
- Block all replacement of host-integrated packages: rejected because it is too restrictive for package updates that remain deterministic and compatible.

## Decision: Use a Nuplane-owned default-context resolving bridge for host-integrated visibility

**Rationale**: Host applications should not register custom assembly resolving handlers. Nuplane can own the process-wide resolving hook behind the loading module and route requests only to active host-integrated assembly resolution entries. This keeps behavior centralized and observable while satisfying framework `Assembly.Load` by-name scenarios.

**Alternatives considered**:
- Require each host to subscribe to resolving events: rejected by feature goal.
- Load all package assemblies directly into the default context: rejected as the first-choice design because it maximizes compatibility but removes more isolation than necessary and cannot be reversed for already-loaded identities.
- Use only non-collectible package-specific contexts without a default-context resolving bridge: rejected because framework by-name resolution would remain unsolved.

## Decision: Keep source trust and graph resolution unchanged before load mode behavior

**Rationale**: Load mode changes assembly lifetime and visibility, not package trust. Existing source validation, package graph resolution, integrity checks, and transactional apply boundaries remain the gate before assemblies can be loaded or made visible.

**Alternatives considered**:
- Add separate trust configuration for host-integrated packages: deferred because the current feature does not introduce new package sources and should not duplicate source trust policy.

## Decision: Options validation stays in the loading options pipeline

**Rationale**: The constitution requires `IValidateOptions<T>` and startup fail-fast for runtime options. New load mode properties must be data-only on options and validated by the loading validator adapter already registered with `ValidateOnStart()`.

**Alternatives considered**:
- Add `IsValid()` to options: rejected by repository rules.
- Validate only when first package loads: rejected because invalid load mode configuration should fail before runtime reconciliation work begins.
