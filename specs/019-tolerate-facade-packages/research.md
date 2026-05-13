# Research: Tolerate Facade Packages

## Decision: Treat No-Assembly Graph Members As Skipped Dependencies

**Rationale**: NuGet packages can legitimately contain no managed assembly for the host to load. Facade/support packages such as SQLite provider dependencies may include placeholder assets like `_._` to indicate compatibility without providing a runtime assembly in that package. Failing the whole graph in that case prevents otherwise valid plugin packages from loading.

**Alternatives considered**:
- Fail the graph whenever any package lacks an assembly. Rejected because it reproduces the SQLite provider failure.
- Create successful sessions for skipped packages. Rejected because no assembly was loaded and downstream package assembly catalogs should only expose loaded packages.

## Decision: Preserve Existing Failure Behavior For Non-Facade Errors

**Rationale**: Missing install paths, incompatible framework assets, ambiguous assembly layouts, and load exceptions indicate real package or environment failures. These failures should continue to mark the graph failed so operators keep LKG protection and actionable diagnostics.

**Alternatives considered**:
- Treat all file-not-found outcomes as skipped. Rejected because missing package installation is not the same as an intentionally empty package.
- Add configuration to opt into facade tolerance. Rejected because facade packages are valid package graph members and should not require host-specific configuration.

## Decision: Publish Host-Integrated Assembly Resolution Only For Loadable Members

**Rationale**: Host-integrated resolution maps assembly names to loaded assemblies. A no-assembly package has no assembly ownership to publish, so including it would add no value and could confuse diagnostics.

**Alternatives considered**:
- Publish empty ownership entries for skipped packages. Rejected because resolution lookups are assembly-centric and empty entries cannot satisfy any request.
