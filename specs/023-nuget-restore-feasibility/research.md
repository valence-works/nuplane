# Research: NuGet Restore Semantics Feasibility

**Date**: 2026-05-13  
**Branch**: `023-nuget-restore-feasibility`

## Decision

Nuplane should reuse NuGet resolver libraries for dependency version solving instead of continuing to expand custom graph/version logic.

The most feasible near-term API is `NuGet.Resolver.PackageResolver` with `DependencyBehavior.Lowest`, fed by `NuGet.Protocol.Core.Types.SourcePackageDependencyInfo` values. This API is public, easy to test in memory, and reproduces the active failure cases:

- lowest applicable dependency version
- direct dependency wins
- cousin dependency unification
- aggregate unification across multiple top-level roots

## Evidence

Added feasibility tests in `test/Nuplane.Runtime.Tests/Feeds/NuGetResolverFeasibilityTests.cs`:

- `Resolve_LowestDependencyBehavior_SelectsLowestApplicableDependencyVersion`
- `Resolve_DirectDependencyWins_ReusesDirectVersionForLowerTransitiveBaseline`
- `Resolve_CousinDependencies_SelectsLowestVersionThatSatisfiesAllRanges`
- `Resolve_MultipleTopLevelRoots_UnifiesSharedDependencyAcrossAggregateGraph`

Validation:

```text
dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj --filter FullyQualifiedName~NuGetResolverFeasibilityTests
Passed: 4
```

## NuGet Packages Evaluated

- `NuGet.Resolver`: Public `PackageResolver` and `PackageResolverContext`. Feasible for solving a pre-collected candidate graph.
- `NuGet.Protocol`: Public feed/resource APIs and `SourcePackageDependencyInfo`. Feasible for collecting package dependency metadata from feeds.
- `NuGet.Packaging`: Public nuspec/package metadata APIs. Feasible for local package metadata extraction and dependency group parsing.
- `NuGet.Versioning`: Already used by Nuplane. Continue using for version/range parsing.
- `NuGet.Frameworks`: Should replace custom TFM compatibility parsing when selecting dependency groups.
- `NuGet.DependencyResolver.Core`: Public `RemoteDependencyWalker` exists, but public feed/provider adapter coverage is awkward. It is closer to PackageReference restore internals, but adopting it directly appears heavier and requires custom `IRemoteDependencyProvider` plumbing.
- `NuGet.Commands`/`NuGet.ProjectModel`: More complete restore machinery, but likely too MSBuild/project-assets oriented for Nuplane's runtime control-plane use case.

## Implemented Architecture

1. Keep the existing root package resolution policy for desired packages.
2. Build an aggregate desired root set for each reconciliation cycle.
3. Collect dependency metadata for candidate package versions during graph discovery.
4. Project candidates into `SourcePackageDependencyInfo`.
5. Filter Nuplane host-provided dependencies before solving.
6. Run `PackageResolver` with `DependencyBehavior.Lowest`.
7. Project the selected identities and dependency relationships back into a single aggregate Nuplane `ResolvedPackageGraph`.
8. Preserve independent root acquisition failures before aggregate graph solving so one missing root does not poison otherwise resolvable roots.

## Open Risks

- `NuGet.Resolver` is documented as the packages.config resolver, while `NuGet.DependencyResolver.Core` is documented as the PackageReference dependency resolver implementation. The feasibility tests show `NuGet.Resolver` matches the required rules for the current cases, but a deeper implementation plan should compare edge cases before final adoption.
- Nuplane currently resolves one graph per desired root. NuGet-like behavior should solve the desired root set as one aggregate graph, then project root-specific graph metadata. This is a behavior change with positive consistency but a broader blast radius.
- The resolver needs a complete enough candidate set. Nuplane must decide whether to enumerate all versions for each discovered dependency or fetch progressively until the lowest compatible solve is possible.
- Target framework group selection should be delegated to `NuGet.Frameworks`/`NuGet.Packaging`; this spike did not yet replace the current custom string parser.

## Rejected Option

Continue growing custom graph resolution rules inside `PackageDependencyGraphResolver`.

Reason: each new bug is another rediscovery of NuGet restore behavior. The current implementation already needs lowest applicable versions, direct wins, cousin unification, multi-root aggregate unification, TFM selection, prerelease handling, exact conflict behavior, and diagnostics. NuGet libraries already encode most of that logic.
