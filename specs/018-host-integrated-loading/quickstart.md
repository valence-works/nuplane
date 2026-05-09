# Quickstart: Host-Integrated Package Loading

## Purpose

Use this quickstart to validate host-integrated loading behavior during implementation. It focuses on observable outcomes, not implementation internals.

## Scenario 1: Default host-integrated loading

1. Configure Nuplane loading as enabled.
2. Set the loading default mode to `HostIntegrated`.
3. Configure shared assemblies for host/plugin contracts separately from load mode.
4. Add a package that contributes framework-discoverable types.
5. Run reconciliation and wait for loading completion.
6. Query the package assembly catalog.
7. Verify the package entry reports `HostIntegrated` and framework-integration safe metadata.
8. Verify host/framework code can discover and activate the contributed types without custom assembly resolving code.

## Scenario 2: Package-specific override

1. Configure the loading default mode as `Collectible`.
2. Add a package-specific override setting one package to `HostIntegrated`.
3. Add one isolated plugin package without an override.
4. Run reconciliation and wait for loading completion.
5. Verify the overridden package is host-integrated and framework-integration safe.
6. Verify the isolated package remains collectible and is not marked framework-integration safe.

## Scenario 3: Assembly-name resolution

1. Load a host-integrated package that contains a known assembly identity.
2. Request the assembly by full name from framework-style code.
3. Verify the expected active package assembly is returned.
4. Request the assembly by simple name when exactly one active host-integrated assembly matches.
5. Verify the expected active package assembly is returned.
6. Review logs or operational state to confirm resolution diagnostics include request identity, selected package, and selected path.

## Scenario 4: Conflict handling

1. Prepare two host-integrated package graphs that expose different versions of the same assembly simple name.
2. Reconcile both packages into the desired active set.
3. Verify activation fails deterministically before conflicting visibility is published.
4. Verify diagnostics identify the assembly simple name, versions, and owning packages.
5. Verify last-known-good active visibility remains unchanged when one exists.

## Scenario 5: Replacement fallback

1. Start with a successfully active host-integrated package version.
2. Reconcile a replacement version that fails during activation or visibility setup.
3. Verify the replacement is not made visible to assembly-name resolution.
4. Verify the prior last-known-good assembly resolution entries remain active.
5. Verify diagnostics identify the replacement failure stage.

## Scenario 6: Invalid configuration

1. Configure an unsupported default load mode value.
2. Start the host.
3. Verify options validation fails before package loading begins.
4. Repeat with duplicate package-specific overrides for the same package ID.
5. Verify validation reports duplicate override diagnostics.

## Required validation commands

Run focused tests first, then broader validation when practical:

```bash
dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj
dotnet test nuplane.sln
```

## Documentation validation

Update the loading section in the README or wiki to explain:

- When to choose `Collectible`.
- When to choose `HostIntegrated`.
- Why shared assemblies solve type identity but do not replace load mode.
- Why host-integrated packages may remain loaded for the process lifetime.
