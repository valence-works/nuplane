# Data Model: Tolerate Facade Packages

## Resolved Package

Existing package graph member with package identity, version, source information, and install path.

## Loadable Graph Package

Internal classification for a resolved package that has a deterministic main managed assembly for the selected host-compatible asset scope.

Fields:
- Package ID
- Version
- Install path
- Main assembly path

Validation rules:
- Install path exists.
- Framework asset selection is compatible with the host.
- Exactly one main assembly is selected, or one assembly matches the package ID.

## Non-Loadable Graph Member

Internal classification for a resolved package that has no managed assemblies in the selected host-compatible asset scope.

Validation rules:
- Only applies to graph loading.
- Does not create a package load session.
- Does not create host-integrated assembly resolution entries.
- Does emit a diagnostic skip log.

## Package Load Session

Existing record of a package whose assemblies were loaded.

State transitions:
- No prior session -> loaded session for loadable graph members.
- No prior session -> no session for skipped non-loadable graph members.
- Prior loaded session -> replaced only when the same package is loaded in a new context.
- Genuine graph failure -> existing failure handling remains unchanged.
