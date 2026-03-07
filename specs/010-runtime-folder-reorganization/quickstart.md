# Quickstart: Runtime Folder & Namespace Reorganization

**Branch**: `010-runtime-folder-reorganization` | **Date**: 2026-03-07

This quickstart describes how to verify the folder/namespace reorganization after each
move is complete. Since this is a pure structural refactor with zero behavior changes,
validation is compilation-based and test-based.

---

## Prerequisites

- Feature branch `010-runtime-folder-reorganization` checked out
- .NET SDK installed (targeting `net8.0`/`net9.0`/`net10.0`)
- All tests passing on the pre-reorganization codebase (`dotnet test` green)

---

## Scenario 1 — Verify Move 1: Feed Acquisition Types

**Goal**: Confirm all feed acquisition types are in `Feeds/` and the solution compiles.

### Steps

1. Verify the new folder structure exists:
   ```bash
   ls src/Nuplane.Runtime/Feeds/
   ls src/Nuplane.Runtime/Feeds/Policy/
   ls src/Nuplane.Runtime/Feeds/Configuration/
   ```

2. Confirm the expected files are present:
   ```bash
   # Feeds/ root — 6 files
   ls src/Nuplane.Runtime/Feeds/MultiFeedPackageResolver.cs
   ls src/Nuplane.Runtime/Feeds/NuGetRemotePackageAcquirer.cs
   ls src/Nuplane.Runtime/Feeds/NuGetPackageResolver.cs
   ls src/Nuplane.Runtime/Feeds/INuGetPackageResolver.cs
   ls src/Nuplane.Runtime/Feeds/NoEligibleFeedException.cs
   ls src/Nuplane.Runtime/Feeds/AcquisitionOutcomeEntry.cs

   # Feeds/Policy/ — 2 files
   ls src/Nuplane.Runtime/Feeds/Policy/FeedResolutionPolicy.cs
   ls src/Nuplane.Runtime/Feeds/Policy/FeedUnavailableException.cs

   # Feeds/Configuration/ — 3 files
   ls src/Nuplane.Runtime/Feeds/Configuration/FeedResolutionOptions.cs
   ls src/Nuplane.Runtime/Feeds/Configuration/FeedResolutionPolicyMode.cs
   ls src/Nuplane.Runtime/Feeds/Configuration/FeedCredentialOptionsValidator.cs
   ```

3. Confirm old folders are cleaned up:
   ```bash
   # Should NOT exist
   ls src/Nuplane.Runtime/Reconciliation/FeedPolicy/ 2>/dev/null && echo "FAIL: FeedPolicy still exists" || echo "PASS"
   ```

4. Confirm namespaces are correct:
   ```bash
   grep -r "namespace Nuplane.Runtime.Feeds;" src/Nuplane.Runtime/Feeds/*.cs
   grep -r "namespace Nuplane.Runtime.Feeds.Policy;" src/Nuplane.Runtime/Feeds/Policy/*.cs
   grep -r "namespace Nuplane.Runtime.Feeds.Configuration;" src/Nuplane.Runtime/Feeds/Configuration/*.cs
   ```

5. Confirm the old `Reconciliation.FeedPolicy` namespace is fully retired:
   ```bash
   grep -r "namespace Nuplane.Runtime.Reconciliation.FeedPolicy" src/ && echo "FAIL" || echo "PASS: namespace retired"
   ```

6. Build and test:
   ```bash
   dotnet build nuplane.sln
   dotnet test nuplane.sln
   ```

### Pass Criteria

- All 11 feed files are in `Feeds/` hierarchy with correct namespaces.
- `Reconciliation/FeedPolicy/` folder no longer exists.
- `namespace Nuplane.Runtime.Reconciliation.FeedPolicy` appears nowhere in `src/`.
- Solution compiles with zero errors.
- All tests pass.

---

## Scenario 2 — Verify Move 2: Desired-State Source Types

**Goal**: Confirm all desired-state types are consolidated in `Sources/` and the `Desired/` folder is removed.

### Steps

1. Verify the moved files are in `Sources/`:
   ```bash
   ls src/Nuplane.Runtime/Sources/DesiredManifestPackageSource.cs
   ls src/Nuplane.Runtime/Sources/DesiredManifestReader.cs
   ls src/Nuplane.Runtime/Sources/DesiredStateAggregator.cs
   ls src/Nuplane.Runtime/Sources/IDesiredStateAggregator.cs
   ls src/Nuplane.Runtime/Sources/StaticDesiredSource.cs
   ls src/Nuplane.Runtime/Sources/DesiredAggregateResult.cs
   ls src/Nuplane.Runtime/Sources/DesiredReadResult.cs
   ```

2. Confirm the `Desired/` folder is removed:
   ```bash
   ls src/Nuplane.Runtime/Desired/ 2>/dev/null && echo "FAIL: Desired/ still exists" || echo "PASS"
   ```

3. Confirm the old `Nuplane.Runtime.Desired` namespace is fully retired:
   ```bash
   grep -r "namespace Nuplane.Runtime.Desired" src/ && echo "FAIL" || echo "PASS: namespace retired"
   grep -r "using Nuplane.Runtime.Desired" src/ test/ && echo "FAIL" || echo "PASS: no using references"
   ```

4. Confirm namespaces:
   ```bash
   grep "namespace Nuplane.Runtime.Sources;" src/Nuplane.Runtime/Sources/DesiredManifestPackageSource.cs
   grep "namespace Nuplane.Runtime.Sources;" src/Nuplane.Runtime/Sources/DesiredStateAggregator.cs
   ```

5. Build and test:
   ```bash
   dotnet build nuplane.sln
   dotnet test nuplane.sln
   ```

### Pass Criteria

- All 7 desired-state files are in `Sources/` with namespace `Nuplane.Runtime.Sources`.
- `Desired/` folder no longer exists.
- `namespace Nuplane.Runtime.Desired` and `using Nuplane.Runtime.Desired` appear nowhere.
- Solution compiles with zero errors.
- All tests pass.

---

## Scenario 3 — Verify Move 3: Trust Gate Types

**Goal**: Confirm trust gate types are in `Trust/` with correct namespaces.

### Steps

1. Verify the moved files:
   ```bash
   ls src/Nuplane.Runtime/Trust/AllowlistGate.cs
   ls src/Nuplane.Runtime/Trust/IAllowlistGate.cs
   ```

2. Confirm namespaces:
   ```bash
   grep "namespace Nuplane.Runtime.Trust;" src/Nuplane.Runtime/Trust/AllowlistGate.cs
   grep "namespace Nuplane.Runtime.Trust;" src/Nuplane.Runtime/Trust/IAllowlistGate.cs
   ```

3. Build and test:
   ```bash
   dotnet build nuplane.sln
   dotnet test nuplane.sln
   ```

### Pass Criteria

- `AllowlistGate.cs` and `IAllowlistGate.cs` are in `Trust/` with namespace `Nuplane.Runtime.Trust`.
- Solution compiles with zero errors.
- All tests pass.

---

## Scenario 4 — Full Reorganization Validation

**Goal**: Verify all success criteria after all three moves are complete.

### Steps

1. Verify Reconciliation folder count reduction (SC-001):
   ```bash
   # Count files directly in Reconciliation/ (not subdirs)
   find src/Nuplane.Runtime/Reconciliation -maxdepth 1 -name "*.cs" | wc -l
   # Should be ~50% fewer than the original ~35 files
   ```

2. Verify fully retired namespaces (SC-005):
   ```bash
   grep -r "Nuplane.Runtime.Desired" src/ test/ --include="*.cs" && echo "FAIL" || echo "PASS"
   grep -r "Nuplane.Runtime.Reconciliation.FeedPolicy" src/ test/ --include="*.cs" && echo "FAIL" || echo "PASS"
   ```

3. Verify removed folders (SC-006):
   ```bash
   ls src/Nuplane.Runtime/Desired/ 2>/dev/null && echo "FAIL" || echo "PASS"
   ls src/Nuplane.Runtime/Reconciliation/FeedPolicy/ 2>/dev/null && echo "FAIL" || echo "PASS"
   ```

4. Verify developer navigation (SC-004):
   ```bash
   # All feed types findable in Feeds/
   find src/Nuplane.Runtime/Feeds -name "*.cs" | sort

   # All desired-state types findable in Sources/
   find src/Nuplane.Runtime/Sources -name "*.cs" | sort

   # All trust types findable in Trust/
   find src/Nuplane.Runtime/Trust -name "*.cs" | sort
   ```

5. Full build and test (SC-002, SC-003):
   ```bash
   dotnet build nuplane.sln
   dotnet test nuplane.sln
   ```

### Pass Criteria

- All 6 success criteria (SC-001 through SC-006) pass.
- Zero compilation errors.
- 100% existing tests pass with no logic changes.

---

## Scenario 5 — Verify Test Folder Mirroring

**Goal**: Confirm test project folder structure mirrors source reorganization.

### Steps

1. Verify test files moved to `Sources/`:
   ```bash
   ls test/Nuplane.Runtime.Tests/Sources/DesiredAggregationContractTests.cs
   ls test/Nuplane.Runtime.Tests/Sources/DesiredManifestParserTests.cs
   ls test/Nuplane.Runtime.Tests/Sources/DesiredStateAggregatorTests.cs
   ```

2. Verify `Desired/` test folder is removed:
   ```bash
   ls test/Nuplane.Runtime.Tests/Desired/ 2>/dev/null && echo "FAIL" || echo "PASS"
   ```

3. Verify trust test mirroring:
   ```bash
   ls test/Nuplane.Runtime.Tests/Trust/AllowlistGateTests.cs
   ```

4. Build and test:
   ```bash
   dotnet test nuplane.sln
   ```

### Pass Criteria

- Test folders mirror the new source structure.
- `Desired/` test folder no longer exists.
- All tests pass.

