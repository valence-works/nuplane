# Contract: Host-Integrated Package Loading

## Scope

This contract defines the expected behavior for package load mode configuration, package assembly catalog metadata, and Nuplane-owned assembly-name resolution for host-integrated packages.

## Configuration Contract

1. Loading configuration MUST support a default package load mode.
2. The default load mode MUST accept `Collectible` and `HostIntegrated`.
3. The default load mode MUST remain `Collectible` when not explicitly configured.
4. Loading configuration MUST support package-specific load mode overrides when package-level configuration is available.
5. Package-specific overrides MUST take precedence over the default load mode.
6. Invalid load mode values MUST fail options validation before loading begins.
7. Duplicate package overrides for the same package ID MUST fail options validation.
8. Shared assembly policy MUST remain configured separately from load mode.

## Loading Contract

1. `Collectible` mode MUST preserve existing collectible package load behavior.
2. `HostIntegrated` mode MUST load package assemblies so returned assemblies are safe for non-collectible host/framework code.
3. Host-integrated loading MUST keep dependency graph resolution consistent with the resolved package graph.
4. Host-integrated activation MUST fail before visibility publication when active host-integrated packages expose different versions of the same assembly simple name.
5. Failed host-integrated activation MUST preserve last-known-good package and assembly resolution visibility.
6. Host applications MUST NOT be required to register custom assembly load context or assembly resolving handlers for intended host-integrated packages.

## Catalog Contract

1. The existing package assembly catalog MUST remain the canonical host-facing package assembly discovery surface.
2. Catalog results MUST include the effective load mode for each active package entry.
3. Catalog results MUST indicate whether each entry is safe for framework integration.
4. Host-integrated package entries MUST be marked framework-integration safe.
5. Collectible package entries MUST not be marked framework-integration safe.
6. Existing package-grouped deterministic ordering MUST be preserved.

## Assembly Resolution Contract

1. Nuplane MUST maintain active resolution entries for host-integrated package assemblies.
2. Framework by-name assembly resolution MUST succeed when the requested identity uniquely matches an active host-integrated package assembly.
3. Resolution by full name MUST honor the requested version and identity.
4. Resolution by simple name MUST fail deterministically when multiple active host-integrated versions could match.
5. Resolution failures MUST emit structured diagnostics for not found, ambiguity, conflict, and inactive package state.
6. Replacement visibility MUST switch to a new generation only after package activation and visibility setup complete successfully.
7. If replacement activation or visibility setup fails, the previous last-known-good resolution generation MUST remain active.

## Observability Contract

1. Loading logs MUST include selected load mode, package identity, package version, graph key, and selection reason.
2. Host-integrated resolution logs MUST include requested assembly identity, outcome, selected package identity, and selected assembly path when successful.
3. Conflict logs MUST include conflicting package identities and assembly versions.
4. Metrics or operational state MUST expose host-integrated load success/failure and resolution failure counts where the loading module already reports loading health.
5. Diagnostics MUST not log secrets, credentials, or full exception stack traces at Information level.
