# Codebase quality, DX, architecture, and structure review

**Issue:** [#104](https://github.com/valence-works/nuplane/issues/104)  
**Stage:** 1 analysis complete; Stage 2 work unit 1 delivered
**Audited commit:** `95af2dc4c998ab213b9aaf18a69324e8178d9262` (`main`, 2026-10-04)  
**Review date:** 2026-10-04

## Executive conclusion

The concern that Nuplane is broadly difficult to maintain is **not supported** by this review. The repository has clear project boundaries, no project-reference cycles, deterministic ordering in important paths, a decomposed reconciliation pipeline, a strong automated-test baseline, and a working sample. A blanket rewrite would discard useful structure without addressing the real risks.

The review did find five high-severity correctness or safety gaps:

1. persisted store state is written by truncating the canonical file and in-memory state advances before durable persistence succeeds;
2. a long-lived process can overwrite another process's newer store state from its stale cache;
3. lock-file generate, version/feed enforcement, and hash enforcement are not connected end to end;
4. documented feed-trust policy is not enforced by the runtime;
5. multiple configured instances of the same desired-state source share one persistence and diagnostic identity.

The remaining findings are bounded maintainability and DX problems: two large composition/lifecycle types, duplicated test support, and current documentation that no longer matches the sample, routes, packages, or public API. These should be fixed incrementally. They do not justify restructuring the whole solution.

## Audit record

### Scope and method

The review covered production and test projects under `src/`, `test/`, and `samples/`; root build/package configuration; CI; `README.md`; `docs/coding-conventions.md`; user-facing wiki pages; and relevant feature specs. It combined:

- code-graph architecture, symbol, call-path, and complexity inspection;
- exact-source review in an isolated managed worktree pinned to the audited commit;
- default-branch issue and pull-request refresh to avoid duplicating active work;
- fresh restore, Release build, Release tests, Release pack, and secret validation;
- a live, session-owned sample process on a unique port;
- replay of the documented package-drop and catalog-query workflows;
- comparison of advertised public types, namespaces, packages, routes, and project paths with the compiled source.

Issue #104 was the only open issue and there were no open pull requests at the start of the audit. PR #103 was the most recent merged change and its merge commit is the audited commit.

### Environment

| Item | Value |
| --- | --- |
| OS | macOS 26.6, Darwin arm64 |
| SDK | .NET SDK 10.0.300; runtime 10.0.8 |
| Build coordination | Repository/shared `dotnet` build-slot wrapper |
| Checkout | Clean isolated managed worktree, detached at the audited commit before the report branch was created |
| Review workroom | Root integration/QA used `gpt-6.1-sol` as the available Sol fallback; bounded architecture, code-quality, and DX audits used `gpt-5.6-luna` at extra-high reasoning |
| Network | GitHub issue/PR/check metadata was refreshed; no new network clone was performed |

### Verification performed

| Check | Result |
| --- | --- |
| `dotnet restore nuplane.sln` | Passed |
| `dotnet build nuplane.sln --configuration Release --no-restore` | Passed, 0 warnings, 0 errors |
| `dotnet test nuplane.sln --configuration Release --no-restore --no-build` | Passed, 1,259 tests, 0 failed, 0 skipped |
| `dotnet pack nuplane.sln --configuration Release --no-restore` | Passed; warnings were limited to non-packable sample projects and missing sample-package readmes |
| `./build/validate-secrets.sh` | Passed |
| Exact audited-revision GitHub validation | PR #103 `build-test` passed; post-merge package and wiki workflows passed |
| Sample host | Started and returned HTTP 200 from current catalog/admin routes |
| Isolated package-feed workflow | Sample package installed and `Nuplane.Sample.Plugin.HelloPlugin` appeared at `/catalog/plugins` |
| Documented repository-root package drop | Reproduced failure: the sample watched its content-root `packages` directory instead |
| Documented legacy loading routes | Reproduced HTTP 404; current route names returned HTTP 200 |

### Limitations

- Failure-atomic persistence and two-process stale-state scenarios were established from source paths but were not fault-injected during Stage 1. Their first remediation work unit adds the missing regression tests before changing behavior.
- Feed-trust and lock-file findings compare current executable paths with current public documentation and options. Stage 1 did not contact an external NuGet feed or activate malicious content.
- The code graph indexes the canonical repository checkout and was used for discovery. Every reported source location was then checked in the exact audited worktree; exact-worktree source is authoritative where they differ.
- macOS arm64 was exercised locally. Linux was covered by the audited revision's GitHub validation; Windows behavior was not exercised.

## Stage 2 delivery record

Stage 2 evidence is recorded separately from the audited Stage 1 baseline above. A remediation PR changes the finding on `main` only after it is reviewed and merged.

| Work unit | Finding IDs | Status | Delivery evidence |
| --- | --- | --- | --- |
| 1 | AR-001, AR-002 | Delivered and merged | [PR #106](https://github.com/valence-works/nuplane/pull/106), reviewed head `a35fb6320b16405360a3964c36af640a33b461db`, squash merge `846c3b336ddfe0c00cbb936226c317c62bec13a5`. Local Release build passed with 0 warnings/errors; all 1,269 tests passed; pack, secret scan, and diff check passed. Exact-head GitHub `build-test` and state-persistence jobs passed on Ubuntu, macOS, and Windows. Final adversarial review reported no remaining Critical/Required finding. Copilot review was requested through both the documented CLI route and direct REST reviewer identity, but GitHub exposed no pending request, review, or comment; no Copilot pass is claimed. |

The first Windows matrix run failed because `File.Move(..., overwrite: true)` denied replacement while a shared reader held the old state file open. The corrected exact head uses `File.Replace` with a unique backup for existing files and `File.Move` only for first creation. If replacement removes the destination and then fails, the prior backup is restored before the error is rethrown; if restoration is blocked, recovery artifacts are retained. The refreshed Windows job passed that production path. The deliberate extra file read before each file-backed mutation protects direct registry writers that loaded before another writer committed, while the lock-held cycle refresh protects reconciliation and startup-recovery reads.

## Architecture and dependency map

```text
Nuplane.Abstractions
        ^
        |
Nuplane (core reconciliation, feeds, store, state, lock file, observability)
  ^               ^                         ^
  |               |                         |
Directory source  Nuplane.Loading           Nuplane.Admin
                  ^          ^                         ^
                  |          |                         |
     Loading.Abstractions    Loading.Api               Admin.Api
```

| Project | Responsibility and dependency direction |
| --- | --- |
| `Nuplane.Abstractions` | Public DTOs and contracts; no project dependencies. |
| `Nuplane` | Core builder, feed resolution, reconciliation, store/state, lock file, recovery, and observability. Depends on abstractions and Microsoft.Extensions primitives. |
| `Nuplane.Sources.Directory` | Optional local-directory desired-state source; depends inward on `Nuplane`. |
| `Nuplane.Loading.Abstractions` | Loading/query contracts; depends only on abstractions. |
| `Nuplane.Loading` | Optional assembly/dependency loading; depends on core and loading abstractions. |
| `Nuplane.Loading.Api` | ASP.NET read surfaces over loading abstractions. |
| `Nuplane.Admin` | Optional administration services over core. |
| `Nuplane.Admin.Api` | ASP.NET endpoints over admin and abstractions. |

No project-reference cycles were found. Optional modules depend inward; core does not depend on loading or administration modules.

`ReconciliationService` is the composition root for a fixed seven-stage pipeline: desired-state read, package resolution, trust/lock gate, diff/events, transaction execution, cleanup, and health/metrics. That fixed order is a useful auditable invariant. Cross-process exclusion covers the full cycle when a store lock is available; single-flight adds in-process coalescing.

## Strengths and intentional tradeoffs

- Package, feed, source, graph, version, and cleanup ordering are explicitly deterministic.
- Package extraction uses staging directories and publish markers, avoiding exposure of half-extracted installs.
- Reconciliation execution has focused middleware rather than one monolithic execution method.
- Optional directory, loading, and admin modules remain outside the core dependency direction.
- DI registration generally follows concrete-first registration with interface factory aliases.
- Startup recovery validates persisted package and graph state under the store lock.
- Loading rollback cleans up contexts, catalogs, sessions, and inert markers on failures.
- Host-integrated loading's non-collectible lifetime is documented and tested as intentional.
- The low-level reconciliation constructor can omit a store lock. This is documented as a testing/composition escape hatch and is a caller responsibility, not a defect.
- A fixed internal middleware order and a large composition root trade pluggability for an easy-to-audit safety sequence. The constructor coupling is debt, but the fixed order itself should be preserved.

## Finding classification and priority

Severity indicates impact, not implementation size:

- **High:** can violate integrity, deterministic reconciliation, security policy, LKG/state safety, or produce materially wrong runtime state.
- **Medium:** blocks a representative consumer/contributor workflow or creates concentrated change risk.
- **Low:** bounded correctness edge case or maintainability/feedback cost.

| ID | Category | Severity | Classification | Summary | Stage 2 disposition |
| --- | --- | --- | --- | --- | --- |
| AR-001 | Architecture/state | High | Confirmed defect | State-file writes are non-atomic and memory advances before durable save | Delivered in PR #106; reviewed head `a35fb632`, squash merge `846c3b33` |
| AR-002 | Architecture/concurrency | High | Confirmed defect | Long-lived processes can overwrite newer persisted state from a stale cache | Delivered with AR-001 in PR #106; reviewed head `a35fb632`, squash merge `846c3b33` |
| AR-003 | Architecture/integrity | High | Confirmed defect | Lock-file generate, enforce, and hash semantics are disconnected from acquisition/activation | Contract decision, then remediate |
| AR-004 | Architecture/security | High | Confirmed defect | Advertised feed-trust policy has no runtime evaluator | Compatibility decision, then remediate |
| CQ-001 | Correctness | High | Confirmed defect | Same-type desired sources collide in snapshots and diagnostics | Remediate after state writer |
| CQ-002 | State provenance | Low | Confirmed debt | Successful empty source snapshots are restored as unavailable even though the current effective desired set is unchanged | Fix with CQ-001 |
| CQ-003 | Maintainability | Medium | Debt | `PackageLoader` owns too many loading lifecycle concerns | Incremental extraction after safety work |
| CQ-004 | Maintainability | Medium | Debt / intentional tradeoff boundary | `ReconciliationService` composition has a broad, duplicated dependency surface | Add a focused factory without changing order |
| TST-001 | Tests/structure | Low | Debt | Desired-state and cross-project test support are duplicated and miss the source-identity regression | Consolidate only where used by fixes |
| DX-001 | Consumer DX | Medium | Confirmed ergonomic defect | Exact getting-started package-drop commands target the wrong directory | Correct and replay |
| DX-002 | Consumer DX | Medium | Confirmed ergonomic defect | README/XML docs advertise nonexistent APIs, namespaces, and packages | Correct and compile-check |
| DX-003 | Consumer DX | Medium | Confirmed ergonomic defect | Catalog quickstart uses obsolete routes | Correct and smoke-check |
| DX-004 | Contributor DX/structure | Low | Confirmed ergonomic defect | Architecture/convention docs reference retired projects and broken/local paths | Correct and link-check |
| DX-005 | CI | Low | Feedback debt | CI passes while executable docs and sample workflow are broken | Add focused drift gates |

## Detailed findings

### AR-001 — State persistence is not failure-atomic

**Evidence and reproduction path**

- [`StoreStateSerializer.SaveAsync`](../../src/Nuplane/Store/State/StoreStateSerializer.cs#L43-L53) opens the canonical state file with `File.Create`, which truncates it before serialization completes.
- [`StoreRegistry.PersistActiveVersionsAsync`](../../src/Nuplane/Store/State/StoreRegistry.cs#L130-L187), `PersistFailureAsync`, and `PersistSourceSnapshotAsync` assign `_currentState` before `SaveAsync` succeeds.
- [`NuplaneStore`](../../src/Nuplane/NuplaneStore.cs#L20-L60) already warns readers that they may observe sharing violations or partial JSON.
- [`DesiredSourceSnapshotCache.SaveAsync`](../../src/Nuplane/Sources/DesiredSourceSnapshotCache.cs#L19-L29) publishes its in-memory snapshot before registry persistence succeeds, so the same process may consume a non-durable snapshot after a failed save.

A cancellation, process crash, disk-full condition, or serializer/write failure can leave a truncated/partial canonical file while the process reports the new state from memory. This conflicts with the repository's transactional and LKG guardrails.

**Root cause:** the file and in-memory representations do not participate in a commit protocol.

**Recommended correction:** serialize to a unique sibling temporary file, flush it, atomically replace/rename the target, clean up safely, and publish the candidate to `_currentState` only after durable replacement succeeds. Publish source snapshots to the in-memory cache only after that persistence step succeeds. Keep one platform-aware persistence helper rather than duplicating atomic-write mechanics.

**Compatibility risk:** atomic replacement and file-sharing behavior differ on Windows and Unix. External readers must continue to see either the old or new complete record. Temporary-file cleanup must not delete another writer's file.

**Acceptance checks:**

- injected serialization/write/cancellation failures leave the prior file valid and `_currentState` unchanged;
- a failed source-snapshot save does not make the non-durable snapshot available from the in-memory cache;
- concurrent readers observe old or new complete JSON, never partial JSON;
- a crash/failure between temporary write and replace leaves at least one valid state record;
- temporary artifacts are scoped and cleaned safely;
- Linux, macOS, and Windows CI exercise the atomic replace behavior.

### AR-002 — Cross-process locking does not refresh cached state

**Evidence and reproduction path**

- [`StoreRegistry.EnsureLoadedUnderLockAsync`](../../src/Nuplane/Store/State/StoreRegistry.cs#L266-L281) reads disk only while `_loaded` is false.
- All three persistence methods perform read-modify-write against `_currentState` after that one-time load.
- [`ReconciliationService`](../../src/Nuplane/Reconciliation/ReconciliationService.cs#L242-L273) acquires the cross-process store lock, but lock acquisition does not refresh the registry.

Process B can load generation N, process A can acquire the lock and save generation N+1, then process B can acquire the lock and overwrite unrelated N+1 package, failure, or source-snapshot fields from its stale generation N cache. The lock serializes writers but does not establish a fresh read.

**Root cause:** the in-process cache lifetime is longer than the cross-process critical section.

**Recommended correction:** for file-backed state, refresh from disk once at the start of every lock-held reconciliation/mutation cycle, then apply all changes to that generation. A persisted generation/CAS token is an alternative if later performance evidence justifies the complexity. Preserve the current cached behavior for explicit in-memory mode.

**Compatibility risk:** one additional disk read per cycle and changed merge behavior where callers accidentally relied on stale state. Refresh must happen inside the same cross-process lock as the write.

**Acceptance checks:**

- two independent registries sharing one file retain both processes' unrelated updates;
- “load once, external write, then persist” cannot remove the external update;
- package, failure, and source-snapshot mutations are covered;
- in-memory/no-file mode remains deterministic and does not perform disk I/O.

### AR-003 — Lock-file behavior is not end-to-end

**Original evidence and execution path (before remediation)**

- [`MultiFeedPackageResolver`](../../src/Nuplane/Feeds/MultiFeedPackageResolver.cs#L125) resolves/acquires a concrete package before lock evaluation.
- [`LockFileCoordinator.EvaluateAsync`](../../src/Nuplane/Reconciliation/LockFileCoordinator.cs#L19-L55) replaces version and feed from a lock entry but preserves the original package's install path.
- [`TrustAndLockGateMiddleware`](../../src/Nuplane/Reconciliation/Middleware/TrustAndLockGateMiddleware.cs#L20-L47) carries only the rewritten `ResolvedPackage`; it drops the expected hash returned by the coordinator and does not rebuild the dependency graph.
- [`PackageApplyExecutor.ExecuteTransactionsAsync`](../../src/Nuplane/Reconciliation/PackageApplyExecutor.cs#L507-L581) creates transaction requests with only ID, version, and correlation ID. Production does not provide the optional transaction stage callbacks; package acquisition has already happened earlier in the resolver.
- [`PackageDependencyGraphResolver`](../../src/Nuplane/Reconciliation/PackageDependencyGraphResolver.cs#L430) records `PackageContentHash: null`.
- [`PackageTransactionCoordinator`](../../src/Nuplane/Store/Transactions/PackageTransactionCoordinator.cs#L34-L70) compares hashes only when both values are present. Its stage callbacks are optional, and production does not provide one.
- `LockFileStore.WriteAsync` has no production caller, so `Generate` mode does not generate or refresh a lock file.

An enforce/strict cycle can claim the locked version/feed while retaining the originally resolved version's install path. Graph keys can then disagree with rewritten packages. Hash mismatch cannot block because the expected hash is dropped and an actual hash is not computed. Generate mode returns an allowed outcome but never writes entries.

**Root cause:** lock-file evaluation was added as a post-resolution metadata gate rather than as a constraint on resolution, acquisition, graph construction, and activation provenance.

**Remediation status (2026-10-05): implemented in [PR #107](https://github.com/valence-works/nuplane/pull/107), with local and GitHub validation complete.** ADR-0001 records the accepted schema `2.0` contract and legacy policy. The runtime now captures one immutable lock snapshot per cycle; constrains locked root, dependency, and contributed-root requests before acquisition; hashes the exact `.nupkg` bytes as canonical `sha512:<standard-padded-base64>`; persists and propagates the actual hash into resolved packages, graph nodes, and transaction requests; and fails closed before activation when the selected version, feed, or hash disagrees with the entry. Generate mode publishes the complete successful closure through the same failure-atomic writer used by store state and preserves timestamps for byte-stable repeated output. Enforce still permits packages without entries, while Strict requires every acquired root and dependency; unsupported schema `1.0` and invalid used hashes produce migration diagnostics. Copilot was requested and Greptile was explicitly triggered, but neither integration started a review; no automated-review result is claimed.

Validation on the integrated branch: `dotnet build nuplane.sln --no-restore` completed with zero warnings and errors; `dotnet test nuplane.sln --no-restore` passed all 1,317 tests (794 runtime, 155 integration, 250 loading, 72 store, 25 NuGet, and 21 directory-source tests). The PR's GitHub Validate workflow also passed build/test and the Ubuntu, macOS, and Windows state-persistence jobs. Focused coverage includes exact local and remote archive bytes, cached metadata validation, root/dependency graph propagation, pre-acquisition lock constraints, strict dependency closure, deterministic generation, malformed-lock diagnostics, and failed atomic replacement preserving the prior lock.

**Correction applied and decisions recorded:** a lock entry is now one provenance constraint `(id, version, feed, hash)` applied before acquisition and dependency-graph construction. ADR-0001 versions the canonical artifact-hash format and legacy/missing-hash behavior. Expected and actual hashes reach root and dependency transactions, Generate writes only a complete successful closure, and the existing public option names remain intact.

**Compatibility risk:** enforce mode will begin failing cases that currently proceed with inconsistent metadata; generate output may expose a previously dormant format; existing lock hashes may not match the chosen canonical representation.

**Acceptance checks:**

- a version/feed mismatch acquires the exact locked artifact before graph construction;
- install path, descriptor, graph node, active state, feed, version, and hash agree;
- matching hashes activate; mismatched root or dependency hashes fail before state/pointer mutation and preserve LKG;
- generate mode writes stable ordering and hashes only after successful resolution;
- generation replaces the lock file atomically and a failed write preserves the prior valid file;
- strict missing-entry behavior covers the complete dependency closure;
- repeated reconciliation from the same inputs is byte-for-byte deterministic where timestamps are excluded or normalized.

### AR-004 — Feed trust is documented but not enforced

**Evidence**

- [`FeedDefinition`](../../src/Nuplane.Abstractions/FeedDefinition.cs#L1-L20) is documented as including trust but contains name, service index, and credentials only.
- [`FeedCredentialOptionsValidator`](../../src/Nuplane/Feeds/Configuration/FeedCredentialOptionsValidator.cs#L5-L65) describes trust-policy validation but validates names, credentials, and HTTPS, not a trust level.
- [`TrustAndLockGateMiddleware`](../../src/Nuplane/Reconciliation/Middleware/TrustAndLockGateMiddleware.cs#L7-L50) invokes only lock-file evaluation.
- [`PackageTransactionRequest`](../../src/Nuplane/Store/Transactions/PackageTransactionRequest.cs#L15-L24) exposes `BlockedByTrustPolicy`, but production transaction construction leaves it at the default.
- README operator guidance tells users to configure `Trusted`, `Restricted`, or `Untrusted` feeds and scoped override reasons, and metrics/logging types expose trust decisions. No production evaluator supplies those decisions.

The runtime can resolve, download, and activate from a configured source without the advertised trust classification or override checks. HTTPS validation is useful transport hygiene but is not the documented trust policy.

**Root cause:** trust vocabulary and test seams exist, but the feed model and effective admission path were never connected.

**Recommended correction and required design decision:** add explicit trust metadata and a source-admission evaluator that runs before remote acquisition/activation. For backward compatibility, the recommended migration is to treat legacy feeds without a trust value as `Trusted` for one documented compatibility window while emitting an actionable warning; explicit `Untrusted` must fail closed unless a narrowly scoped, reason-bearing override applies. This task becomes implementation-ready after that default/migration is accepted and recorded.

**Compatibility risk:** fail-closed policy changes can prevent previously accepted packages; configuration and public model additions require additive defaults and secret-safe diagnostics.

**Acceptance checks:**

- explicit untrusted feeds cannot resolve/download/activate by default;
- restricted/trusted behavior matches documented policy;
- overrides are package/feed-rule scoped, require an operator reason, and do not leak to dependencies;
- effective trust is tested for multiple configured feeds and directory sources;
- logs/metrics report decisions without credentials or secret-provider values.

### CQ-001 — Desired-source identity collides by implementation type

**Evidence and reproduction path**

- [`DesiredStateReadMiddleware`](../../src/Nuplane/Reconciliation/Middleware/DesiredStateReadMiddleware.cs#L53-L89) derives `SourceName` from `source.GetType().FullName` and uses it as the snapshot save/load key.
- [`DesiredStateAggregator`](../../src/Nuplane/Sources/DesiredStateAggregator.cs#L44-L68) uses the same type-name key for errors, so later same-type errors overwrite earlier ones.
- Feed and directory registration create multiple configured instances of the same implementation type in [`NuplaneFeedRegistrationServices`](../../src/Nuplane/Feeds/Registration/NuplaneFeedRegistrationServices.cs#L50-L55) and [`DirectorySourceRegistrationServices`](../../src/Nuplane.Sources.Directory/Registration/DirectorySourceRegistrationServices.cs#L72-L87).
- `IDesiredPackageSource` exposes no stable per-instance identity.

Two configured instances can overwrite each other's successful snapshots. On a later outage, one source may fall back to the other source's snapshot; simultaneous failures retain only one diagnostic entry.

**Root cause:** implementation type was used as registration-instance identity.

**Recommended correction:** introduce an additive optional source-identity contract or registration wrapper. Built-in sources must use stable configured feed/source keys; custom legacy sources may fall back to the type key. Persisted type-name keys need a backward-read/migration path.

**Compatibility risk:** changing the existing interface would break custom sources, so do not add a required member directly. Snapshot keys are persisted and must remain readable during migration.

**Acceptance checks:**

- two same-type sources retain independent snapshots and errors;
- one source's outage falls back only to its own last successful snapshot;
- configured identities are stable across restart and registration order;
- legacy type-name snapshot keys remain readable or are migrated once;
- precedence behavior introduced by PR #103 remains unchanged.

### CQ-002 — Empty successful snapshots lose persisted provenance

[`DesiredSourceSnapshotCache`](../../src/Nuplane/Sources/DesiredSourceSnapshotCache.cs#L19-L71) persists an empty request array but restores only snapshots whose request count is greater than zero. The current middleware produces the same empty effective desired set whether fallback returns `null` or `[]`, so no externally observable runtime error was established. The confirmed problem is loss of stored-success provenance and an inconsistent save/load contract.

**Root cause:** the load predicate conflates an empty value with no persisted value.

**Recommended correction:** treat a non-null requests collection, including an empty collection, as a valid snapshot; reserve `null` for legacy/unavailable state.

**Compatibility risk:** low; only empty persisted snapshots change meaning.

**Acceptance checks:** save/load of `[]` returns a non-null empty snapshot with provenance, while a legacy `null` remains unavailable; existing outage behavior remains unchanged.

### CQ-003 — `PackageLoader` mixes lifecycle and selection responsibilities

[`PackageLoader`](../../src/Nuplane.Loading/PackageLoader.cs#L19-L62) is 1,458 lines and starts with a broad set of lifecycle collaborators and mutable catalogs. [`EnsureGraphLoaded`](../../src/Nuplane.Loading/PackageLoader.cs#L348-L499) combines graph-cache lookup, package resolution, load-context creation, assembly loading, host catalog publication, session bookkeeping, context replacement/unload, inert markers, and rollback. The same type also owns asset/framework selection and nested catalog/parser types.

**Root cause:** loading features accumulated behind one internal facade without extracting pure selection and publication components.

**Recommended correction:** keep the existing `PackageLoader` facade and first extract pure asset/framework selection, then graph load/rollback coordination, then host catalog/session publication. Each extraction must preserve operation order and add focused characterization tests before movement.

**Compatibility risk:** the type is internal, but load-context identity, rollback, and publication order are behaviorally sensitive.

**Acceptance checks:** all loading tests remain green; extracted components have focused tests; `EnsureGraphLoaded` becomes a readable orchestration method; no new public abstraction is introduced without a consumer.

### CQ-004 — Reconciliation composition remains broadly coupled

[`ReconciliationService`](../../src/Nuplane/Reconciliation/ReconciliationService.cs#L72-L232) exposes a roughly 22-parameter public constructor, repeats that broad surface internally, and manually constructs the transaction coordinator, snapshot cache, executor, and seven middleware stages.

This is not an execution god class: execution has already been decomposed. The debt is concentrated composition and test construction. The fixed stage order remains a strength.

**Recommended correction:** add a module-owned immutable composition context/factory used by DI and tests. Preserve the existing public constructor as a compatibility adapter, and keep pipeline order explicit in one place.

**Compatibility risk:** removing or changing the public constructor would break consumers. The task must be additive first.

**Acceptance checks:** normal registration and tests use the focused factory/context; the compatibility constructor produces the identical pipeline; stage order has one canonical contract test.

### TST-001 — Test support is duplicated and misses the key identity case

Four classes cover `DesiredStateAggregator` with repeated fake/faulting sources and overlapping ordering/duplicate scenarios: [`DesiredStateAggregatorTests`](../../test/Nuplane.Runtime.Tests/Sources/DesiredStateAggregatorTests.cs), [`DesiredAggregationContractTests`](../../test/Nuplane.Runtime.Tests/Sources/DesiredAggregationContractTests.cs), [`DesiredAggregationDeterminismTests`](../../test/Nuplane.Runtime.Tests/Sources/DesiredAggregationDeterminismTests.cs), and [`DesiredAggregationDuplicateRegressionTests`](../../test/Nuplane.Runtime.Tests/Sources/DesiredAggregationDuplicateRegressionTests.cs). The contract test that mentions same-type sources supplies only one faulting source, so it cannot detect CQ-001. Near-identical [`ReconciliationServiceFactory`](../../test/Nuplane.Runtime.Tests/ReconciliationServiceFactory.cs) and `ReconciliationServiceTestExtensions` implementations exist in Runtime and Integration tests; `TempDirectory` is also duplicated in Runtime and Directory-source test support.

**Recommended correction:** consolidate desired-source fixtures as part of CQ-001/CQ-002, and move genuinely cross-project helpers to one internal test-support project or linked source only when a remediation touches them. Avoid a standalone cleanup PR that changes no test behavior.

**Compatibility risk:** test-only; a shared support project must not introduce production dependencies or hide scenario-specific arrange steps.

**Acceptance checks:** the two-instance regression exists; each behavior has one canonical test location; duplicated helper implementations touched by remediation are removed; focused and full suites remain green.

### DX-001 — Getting-started commands target the wrong watched directory

[`README.md`](../../README.md#L635-L676) and [`docs/wiki/Getting-Started.md`](../wiki/Getting-Started.md#L9-L21) tell a repository-root user to copy the sample package into `./packages`. The sample's relative directory is resolved from the ASP.NET content root, so it watches `samples/Nuplane.Sample.AspNetCore/packages`.

The exact documented commands produced an empty package catalog. Copying the same `.nupkg` into the sample content-root directory produced `Nuplane.Sample.Plugin@1.0.0` and `Nuplane.Sample.Plugin.HelloPlugin` without restarting the host.

**Recommended correction:** use the explicit sample path in every root-level command and state which process/content root resolves relative feed paths.

**Compatibility risk:** documentation-only.

**Acceptance checks:** execute the commands verbatim from a clean repository root; `/catalog/packages` and `/catalog/plugins` show the package/type within the documented wait window.

### DX-002 — Current docs advertise nonexistent public APIs and packages

Confirmed mismatches include:

- [README quick start](../../README.md#L241-L255) imports nonexistent `Nuplane.Sources.Directory.Hosting.Builder` and calls nonexistent `OnPackagesLoaded<T>()`; the current API uses directory configuration/registration namespaces and `OnPackagesChanged<T>()`. The same absent loading callback appears in [configuration guidance](../../README.md#L520-L534).
- [README integrity guidance](../../README.md#L737-L755) advertises `IPackageValidator` and `PackageArtifact`, which are absent.
- [README operator guidance](../../README.md#L698-L705) names `Nuplane.Admin.AspNetCore`; the project/package is `Nuplane.Admin.Api`.
- [README architecture](../../README.md#L771-L780) names retired `Nuplane.Runtime`, `Nuplane.Store`, `Nuplane.NuGet`, and `Nuplane.Hosting` projects.
- XML documentation in [`NuplaneBuilderLoadingExtensions`](../../src/Nuplane.Loading/Builder/NuplaneBuilderLoadingExtensions.cs#L8-L12) and [`NuplaneServiceCollectionExtensions`](../../src/Nuplane/NuplaneServiceCollectionExtensions.cs#L74) tells consumers to reference nonexistent `Nuplane.Loading.Hosting`; the package is `Nuplane.Loading` while the public namespace remains `Nuplane.Loading.Hosting.Builder`.
- Spec 009 still describes a host-facing loading observer contract that is deliberately internal and guarded as non-public by `LoadingOwnershipContractTests`.

**Root cause:** documentation was not migrated alongside public API/package simplification.

**Recommended correction:** document only current public, supported surfaces; clearly label historical specs; correct package names without renaming namespaces. If a loading-event hook is still a product requirement, track it separately as an additive API design rather than implying it exists.

**Compatibility risk:** documentation corrections are safe. Exposing an observer API or renaming a namespace would be a separate compatibility decision and is not recommended by this finding.

**Acceptance checks:** extracted README snippets compile in an external sample project; every advertised type/package resolves; historical specs cannot be mistaken for current integration guidance.

### DX-003 — Query quickstart uses obsolete routes

[`specs/014-query-package-catalog/quickstart.md`](../../specs/014-query-package-catalog/quickstart.md#L53-L72) documents `/nuplane/admin/loading`, `/catalog/loading`, and `/catalog/assemblies/{packageId}/{version}`. Current routes are `/nuplane/admin/load-state`, `/catalog/load-state`, and `/catalog/assemblies/{packageId}` in [`NuplaneLoadStateEndpointExtensions`](../../src/Nuplane.Loading.Api/NuplaneLoadStateEndpointExtensions.cs#L17-L29) and the sample endpoint mapper.

The documented legacy routes returned HTTP 404 in the live sample; current routes returned HTTP 200.

**Recommended correction:** update the quickstart to the current contract and replay every request. If exact-version lookup remains required, define it in a new API issue instead of documenting an absent route.

**Compatibility risk:** documentation-only unless a missing route is intentionally restored.

**Acceptance checks:** all quickstart requests run verbatim with expected status and response shape.

### DX-004 — Contributor architecture documentation is stale

[`docs/wiki/Architecture-Guide.md`](../wiki/Architecture-Guide.md#L22-L32) and [`docs/coding-conventions.md`](../coding-conventions.md#L80-L96) map retired project/namespace names. The architecture guide contains broken relative links to removed project directories, and `specs/012-default-state-path/checklists/requirements.md` contains a machine-local absolute path.

**Recommended correction:** replace the map with the dependency map in this review, correct namespaces/package ownership, mark old implementation plans as historical where appropriate, remove the local path, and run a repository-relative link/path check.

**Compatibility risk:** documentation-only.

**Acceptance checks:** all repository-relative links resolve on a clean checkout; current project/package names match `nuplane.sln` and pack output; no machine-local path remains in maintained docs/checklists.

### DX-005 — CI does not exercise executable documentation or sample workflows

[`validate.yml`](../../.github/workflows/validate.yml#L17-L40) restores, builds, and tests. That baseline is valuable, but all 1,259 tests pass while DX-001 through DX-004 remain reproducible.

**Recommended correction:** add fast deterministic checks for relative links/current project paths, compile a maintained external-consumer snippet project, and run a bounded sample HTTP/package-drop smoke test. Keep expensive or flaky network behavior out of PR validation.

**Compatibility risk:** longer CI and potential flakiness. Use local packages, a unique port, explicit readiness polling, bounded timeouts, and guaranteed process cleanup.

**Acceptance checks:** the new gates fail against the stale examples/routes/paths described above, pass after corrections, and complete within the existing workflow timeout with session-owned processes.

## Hypotheses and non-findings

The following are not confirmed defects and must not be reported as fixed without new evidence:

| ID | Hypothesis or concern | Current disposition / evidence needed |
| --- | --- | --- |
| HYP-001 | Process-local `AtomicPointerSwitcher` conflicts with README's on-disk `current/{id}` pointer promise | Decide whether persisted store state or durable symlinks/pointers are canonical. Add restart/failure tests first; then correct implementation or docs. |
| HYP-002 | A later graph-node transaction failure may leave earlier process-local pointers switched | Add fault injection across a multi-node graph and observe state/pointer visibility before changing rollback. |
| HYP-003 | `DesiredStateOptions.SourcePriorities` needs validation for blank/unknown keys | Define whether unmatched keys are intentionally tolerated. Add contract tests before a validator. |
| HYP-004 | No `global.json` makes SDK selection too variable | Current CI and local SDK are explicit enough for this audit. Add pinning only if reproducibility evidence shows drift. |

Concerns disproved or classified as intentional:

- The solution does not have dependency cycles.
- `ReconciliationService` is not an execution god class; middleware decomposition is real.
- Desired-state aggregation has substantial focused coverage; the gap is source-instance identity, not an absence of tests.
- Cancellation is forwarded and distinguished from normal source failures.
- Host-integrated resolver lifetime is intentionally process-wide and non-collectible.
- Styling and naming preferences without measured correctness, discoverability, or change-cost impact are not findings.

## Ordered remediation plan

Each work unit should produce a small coherent PR, update this report's status table, and link the delivered revision from issue #104. A later task may start only when its readiness condition is satisfied.

| Order | Work unit | Finding IDs | Dependencies | Readiness and independent verification |
| --- | --- | --- | --- | --- |
| 1 | Add fault-injection/two-registry tests, then implement one atomic, refresh-on-lock state commit path | AR-001, AR-002 | None | Delivered in PR #106 at reviewed head `a35fb632`, squash merge `846c3b33`; exact-head old/new visibility, destructive replacement failure, cancellation, two-registry, in-memory, reconciliation/recovery refresh, and Ubuntu/macOS/Windows checks passed. |
| 2 | Define lock-file provenance/hash contract in an ADR; constrain resolution before acquisition; implement generate/enforce/strict across roots and dependencies | AR-003 | Work unit 1 for safe lock-file/state persistence patterns | Accepted decisions are recorded in ADR-0001; implementation and local/GitHub validation are complete in PR #107. Copilot and Greptile review integrations did not start, so no automated-review result is claimed. |
| 3 | Record trust compatibility policy; add feed trust model and pre-acquisition admission evaluator | AR-004 | Lock/resolution seam from work unit 2 should be stable | Decision task ready. Recommended default is legacy-unspecified = trusted-with-warning for one compatibility window. Verify explicit untrusted fail-closed and scoped overrides. |
| 4 | Introduce additive source-instance identity with persisted-key migration; restore empty snapshots correctly; consolidate the affected fixtures | CQ-001, CQ-002, TST-001 | Work unit 1 | Ready. Verify two same-type feeds/directories, restart, fallback isolation, error retention, empty snapshot, and legacy key read. |
| 5 | Repair consumer quick starts, routes, API/package names, architecture map, and historical-spec labels | DX-001, DX-002, DX-003, DX-004 | Can run in parallel with work units 1–4 after adopting any overlapping PR | Ready. Verify root-level commands, all HTTP requests, relative links, and external snippet compilation. |
| 6 | Add bounded documentation/link/snippet/sample smoke gates to PR validation | DX-005 | Work unit 5 | Ready after corrected fixtures exist. Prove each gate fails on its targeted regression and passes without network dependency. |
| 7 | Extract pure asset/framework selection and graph lifecycle collaborators behind the existing loader facade | CQ-003 | Safety and DX work complete; no overlapping loader PR | Ready as characterization-led refactor. Preserve ordering/context identity; run all loading and integration tests. |
| 8 | Add a focused reconciliation composition factory/context while preserving the public constructor and fixed stage order | CQ-004 | Work units 1–4 stabilize new dependencies | Ready as additive refactor. Verify compatibility construction and one canonical pipeline-order contract. |
| 9 | Run hypothesis tests and record dispositions; create follow-ups only for confirmed behavior | HYP-001–HYP-004 | Relevant preceding work | Investigation-ready, not implementation-ready. Evidence determines whether code or docs change. |

### Delivery rules for Stage 2

- Refresh `main`, open issues, open PRs, and this report before claiming a work unit. Adopt matching work rather than opening a competing PR.
- Write the regression/contract test first for every confirmed runtime defect.
- Preserve public APIs unless the work unit contains an accepted migration decision.
- Do not combine loader/composition refactoring with state, lock, or trust behavior changes.
- Run focused tests first, then affected projects, then Release solution build/test/pack and representative live workflows.
- Obtain CI/review evidence for each exact PR head. A passing earlier baseline is not delivery proof.
- Do not close issue #104 after the report or an individual PR. Close it only when every confirmed actionable finding has a delivered or explicitly accepted tracked disposition and final evidence is recorded.

## Separate-session handoff

Use the `agentic-program-lead` workflow in a separate code session.

**Objective:** continue the ordered remediation plan above without broad rewrites, starting from current `origin/main` and keeping issue #104 open until all confirmed actionable findings are resolved or explicitly accepted and tracked.

**Inputs:**

- issue: `https://github.com/valence-works/nuplane/issues/104`
- report: `docs/reviews/codebase-quality-review.md`
- audited baseline: `95af2dc4c998ab213b9aaf18a69324e8178d9262`
- report finding IDs: `AR-001`–`AR-004`, `CQ-001`–`CQ-004`, `TST-001`, `DX-001`–`DX-005`, hypotheses `HYP-001`–`HYP-004`
- required repository instructions: `AGENTS.md`, `README.md`, `docs/coding-conventions.md`

**Baseline evidence:** Release restore/build/test/pack and secret validation passed; 1,259 tests passed with no failures/skips; the audited commit's GitHub validation and post-merge publish workflows passed; current sample routes and isolated package discovery worked; the root-package-drop and legacy-route failures were reproduced.

**First-session instructions:**

1. Refresh issue/PR/default-branch/worktree state and confirm that no matching remediation PR has appeared.
2. Read this report in full and preserve its stable finding IDs in PRs and issue updates.
3. Treat work unit 1 as delivered through PR #106 and AR-003 as implemented on its delivery branch; do not duplicate either. Do not begin AR-004 until its recorded design decision satisfies the readiness note.
4. Use isolated managed worktrees and session-owned integration processes; preserve unrelated changes.
5. Link each PR and exact validation evidence from issue #104, update finding status in this report, and leave the issue open until Stage 2 completion criteria are met.

**Outstanding decisions:** compatibility default/window for feeds without explicit trust metadata (AR-004); canonical durable activation pointer model (HYP-001). ADR-0001 records the accepted canonical lock-file hash representation and legacy/missing-hash behavior for AR-003.
