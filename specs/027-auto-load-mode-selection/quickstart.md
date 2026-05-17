# Quickstart: Automatic Load Mode Selection

## Purpose

Use this quickstart to validate automatic load-mode selection during implementation. It focuses on observable behavior and regression coverage, not internal implementation details.

## Scenario 1: No Metadata And No Override

1. Configure Nuplane loading as enabled.
2. Set `DefaultLoadMode` to `Collectible`.
3. Ensure no package-specific override exists for the test graph.
4. Install or fixture a resolved graph with no package-root `nuplane.json`.
5. Run loading for the graph.
6. Verify the effective graph mode is `Collectible`.
7. Verify `LoadingPackageDescriptor` explains the mode with `default`.
8. Verify no host-integrated assembly resolution entries are published for the graph.

## Scenario 2: Explicit Package Override Still Wins

1. Configure `DefaultLoadMode` as `Collectible`.
2. Configure one package-specific `PackageLoadModes` override to `HostIntegrated`.
3. Load a graph containing that package and at least one dependency.
4. Verify the loadable dependency closure is `HostIntegrated`.
5. Verify descriptors distinguish the explicit package with `package-override` and promoted graph members with `dependency-closure`.

## Scenario 3: Package Metadata Promotes The Graph

1. Configure `DefaultLoadMode` as `Collectible`.
2. Do not configure package-specific overrides.
3. Add package-root `nuplane.json` to the root package:

   ```json
   {
     "schemaVersion": 1,
     "loading": {
       "loadMode": "HostIntegrated",
       "scope": "DependencyClosure",
       "reason": "Uses framework type resolution and runtime scheduler integration."
     }
   }
   ```

4. Load the resolved graph.
5. Verify the effective graph mode is `HostIntegrated`.
6. Verify descriptors include `package-metadata` for the declaring package and `dependency-closure` for promoted packages.
7. Verify the graph works without app-specific package override configuration.

## Scenario 4: Explicit Override Suppresses Metadata

1. Configure `DefaultLoadMode` as `Collectible`.
2. Add package metadata requesting `HostIntegrated`.
3. Configure a package-specific override for the same package to `Collectible`.
4. Load the graph with no other host-integrated requirement.
5. Verify the graph remains `Collectible`.
6. Verify descriptors include `metadata-suppressed` identifying the package metadata that was ignored.

## Scenario 5: Invalid Metadata Is Degraded

1. Configure automatic selection.
2. Add malformed or unsupported package-root `nuplane.json` to a package.
3. Load the graph.
4. Verify reconciliation/loading does not crash solely because of the invalid metadata.
5. Verify selection falls back to explicit override or default behavior.
6. Verify descriptors and logs include `metadata-invalid` with the affected package identity.

## Scenario 6: Metadata Conflict Uses Deterministic Safe Mode

1. Create a graph where one package requests `HostIntegrated` and another package declares `Collectible`.
2. Load the graph under automatic selection.
3. Verify the effective graph mode is `HostIntegrated`.
4. Verify diagnostics include `metadata-conflict` or explain the preference/requirement conflict deterministically.
5. Repeat the same load and verify the effective mode and reason codes are identical.

## Scenario 7: Automatic Selection Disabled

1. Set load-mode selection policy to explicit-only.
2. Add package metadata requesting `HostIntegrated`.
3. Do not configure package-specific overrides.
4. Load the graph with `DefaultLoadMode=Collectible`.
5. Verify metadata does not influence the effective mode.
6. Verify descriptors explain that advisor selection was disabled or that default behavior was used.

## Scenario 8: Generic Provider-Style Regression

1. Build or fixture a generic package graph with a root package and provider/runtime dependencies.
2. Add package-root metadata to the root declaring `HostIntegrated` with `DependencyClosure`.
3. Configure `DefaultLoadMode=Collectible` and no package-specific overrides.
4. Load the graph.
5. Verify the effective graph mode is `HostIntegrated`.
6. Verify a framework/default-context style assembly-qualified type resolution scenario can be satisfied by the host-integrated graph.

## Required Validation Commands

Run focused tests first, then broader validation when practical:

```bash
dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj
dotnet test nuplane.sln
```

## Documentation Validation

Update README or wiki loading guidance to explain:

- Package-root `nuplane.json` authoring.
- Automatic selection policy and explicit-only opt-out.
- Existing `PackageLoadModes` migration path.
- Override precedence.
- `Collectible` metadata as preference-only.
- Package metadata trust boundaries.

## Validation Notes

- Focused loading tests were run with:

  ```bash
  dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj --no-restore --filter "FullyQualifiedName~PackageMetadataLoadMode|FullyQualifiedName~PackageLoadModeSelector|FullyQualifiedName~PackageLoaderHostIntegrated|FullyQualifiedName~LoadingCatalog|FullyQualifiedName~LoadingOptionsValidator|FullyQualifiedName~LoadingRegistrationDeterminism|FullyQualifiedName~LoadingOwnershipContract"
  ```

- Covered scenarios include package-root metadata parsing, invalid metadata diagnostics, automatic advisor results, explicit override precedence, dependency-closure promotion, collectible fallback, explicit-only policy, metadata conflict resolution, descriptor diagnostics, and load-mode selection logs.
- Full solution validation was run with:

  ```bash
  dotnet test nuplane.sln
  ```

- The full solution test run passed after the loading-focused validation.
