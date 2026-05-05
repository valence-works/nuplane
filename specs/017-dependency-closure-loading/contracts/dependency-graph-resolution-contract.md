# Contract: Dependency Graph Resolution

## Component

`IPackageDependencyGraphResolver`

## Purpose

Resolve desired package roots into complete package dependency graphs before package acquisition and activation.

## Input

- Desired package roots from `IDesiredStateAggregator`.
- Configured feed definitions and feed priority policy.
- Local directory package candidates.
- Host target framework override, when configured.
- Existing version range and pre-release selection rules.

## Output

- `ResolvedPackageGraph` on success.
- `GraphResolutionFailure` on failure.

## Behavioral Contract

1. The resolver MUST evaluate desired roots in deterministic order by normalized package id and requested version range.
2. The resolver MUST resolve the direct root version using existing direct package version resolution semantics.
3. The resolver MUST read dependency groups from the selected package's NuGet metadata.
4. The resolver MUST choose the dependency group compatible with the host target framework.
5. The resolver MUST recursively resolve each non-optional dependency edge using configured trusted sources only.
6. The resolver MUST deduplicate identical package id/version nodes and preserve every dependency edge that selected that node.
7. The resolver MUST fail the graph when a required dependency edge has no satisfiable package version.
8. The resolver MUST fail deterministically when incompatible dependency ranges cannot be satisfied by one selected version inside the graph boundary.
9. The resolver MUST allow independent desired root graphs to select different versions of the same dependency package when each graph satisfies its own dependency constraints.
10. The resolver MUST detect dependency cycles and fail graph resolution before acquisition.
11. The resolver MUST return sorted nodes, edges, roots, and source decisions so graph identity is stable for unchanged inputs.

## Failure Contract

Failures MUST include:

- desired root package id
- root requested version range
- dependency package id, when applicable
- dependency requested version range
- source or feed names searched
- target framework
- failure stage
- reason code
- cycle path, when dependency metadata contains a cycle

## Test Contract

- Root-only desired configuration resolves the root and its dependency without requiring the dependency to appear in desired input.
- Root with one dependency resolves both nodes.
- Root with transitive dependency resolves all nodes.
- Compatible duplicate dependency edge deduplicates the node.
- Unsatisfiable dependency edge fails graph resolution.
- Independent roots with incompatible dependency versions resolve as side-by-side graphs when each graph is satisfiable.
- Dependency cycle fails graph resolution and reports the cycle path.
- Missing dependency package fails graph resolution.
- Dependency group incompatible with host target framework fails with target-framework diagnostic.
- Local root can resolve a dependency from a configured remote feed.
- Repeated identical inputs produce equal graph identity and sorted graph contents.
