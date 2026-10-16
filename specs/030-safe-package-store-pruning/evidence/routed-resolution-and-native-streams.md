# Routed resolution and native package streams

## Increment and ownership boundary

This increment preserves exact immutable successful graph selections through resolve/apply, publishes graph-use owners under the short root/member and store locks, and defers actual Loading until those short locks are released. The service retains cycle serialization through completion and releases every graph owner even when loading or cleanup fails. Deferred failures are persisted through a fresh short admission, then included in cycle health. The Loading module drains its correlation queue even when a host replaces the Core failure contributor or an observer cancels dispatch; last-failure diagnostics remain available.

The owned resolver now forwards the original operation borrow into the native graph reader. `MultiFeedPackageResolver` forwards that borrow to scoped remote acquisition and probes completion/hash through it. Unknown unscoped acquirers and unsupported local-feed source capabilities refuse before the corresponding callback. These seam tests do not qualify the built-in remote acquisition implementation.

Native archive-read and creation-only write streams own parent/file handle leases independently of their wrapper handles. They use bounded positional IO, exact canonical-name/profile and identity replay, byte quotas, sequential write claims and cancellation checks between partial writes. EOF and zero-byte reads still validate the native binding. The new Windows source compiles locally; actual Windows execution requires the hosted lane.

## Review corrections

Root review and regression checks repaired an array-length compile error, XML documentation, a Windows instance-call compile error, and a stale fixture named argument. A full Runtime run exposed eager construction of an async-only lifetime service in hosts without Loading; resolution is now conditional on an actual leased Loading observer. A contribution-limit refusal left graph selections for removed roots; the surviving closure is now rebuilt after the refusal, with regression assertions on both flat packages and graph root requests.

The old graph-reader admission-refusal fixture is now a malformed-XML test: real scoped native graph reading records a resolution failure, produces a degraded result and preserves the active map. A separate full Runtime run found publication of an ordinary first-install directory between a legacy local completion probe and its strict final absence replay. A bounded process-local striped gate now encloses local probe plus publication, including across resolver instances. This leaves native checks and cross-process authority requirements intact; it is not a substitute for scoped native acquisition.

Loader caches now compare exact install path, archive hash and native install identity for every graph node, including inert nodes. The scoped inert lookup uses the same native projection. An exact cache-hit decision and its reuse/owner transfer run under one monitor; a miss leaves that monitor before activation gates run. This closes a review finding where a competing replacement could turn a gate-free hit into an ungated fresh load. The concurrency test is bounded stress, not a deterministic reproduction of that former interleaving.

A separate protected-state producer constructs complete native-bound active/recoverable candidates from an already admitted member, exact successful selections and explicit failed roots. It preserves failed-root historical subclosures, exact source/path lineage and retirement evidence, then applies the existing complete state verifier. Input collections are copied before awaiting. Every desired root must have a fully active selected graph or explicit failure evidence; an old active graph cannot silently substitute for a selected inactive generation. This producer is not wired into production reconciliation yet.

## Qualification and remaining delivery

Local gate artifacts preserve exact input manifests, logs and TRX results, including initial failures. Removing only the deferred correlation drain caused the corrupt-dependency regression to fail. Removing only the fully-active desired-root coverage check caused both omitted-root and inactive-generation regressions to fail. Exact source bytes were restored after each control. Independent pinned source review found no remaining blocker in candidate construction or the atomic cache-hit fix; it did not claim runtime wiring or complete pruning acceptance.

The final combined run retained the same 904-input manifest SHA-256 `29b42af7121e76173e99a40884bcf25a06fb48ece8a35c3956a42d3d1788a8cf` throughout all ten checks:

| Gate | Passed / failed / skipped |
| --- | --- |
| Retained Loading integration | 16 / 0 / 0 |
| Native resolution readers and scoped seams | 31 / 0 / 0 |
| Protected active-state candidates | 5 / 0 / 0 |
| Native package streams, Unix lane | 3 / 0 / 0 |
| Coordinated runtime and startup | 33 / 0 / 0 |
| Full Store | 638 / 0 / 40 platform-specific |
| Full Runtime | 860 / 0 / 0 |
| Full Loading | 259 / 0 / 0 |
| Core and Loading Release, .NET 8/9/10 | successful, zero warnings/errors |

Strict workflow accounting accepted the exact 31/16/3/5/33-case results and rejected all thirty missing/empty/duplicate/skipped/failed/substituted controls. All five changed bash steps parse, and nineteen embedded Python blocks compile. Workflow qualification SHA-256 `19562161627063f387339c153393d9615c899bbe748228dc22f148740a809b16`; combined log SHA-256 `385d7bb7819220f88a337054aa818e672d8f463e9c76f65d24ed4c349b749bdf`; checkpoint SHA-256 `6a30dfba1b9aba8e9b2f8e0dd4e23aa433e1633b7577a06f84c2e15a2bd1859b`.

An earlier combined run was deliberately terminated after read-only handoff permissions prevented copying the final test snapshot. Its partial results are retained; no complete pass is claimed for it. Current exact-head hosted qualification remains pending. Prior hosted results for `468242d` do not qualify this increment.

The production diff/transaction and active-state publication guards remain until a complete native protected transition replaces them. Native remote acquisition, local-feed source admission, staging/extraction/publication/cleanup, manual inventory/preview/execute, physical deletion and crash recovery remain required. Neither automatic two-composition routing, pruning acceptance, stable releases nor Foundation adoption is claimed here.
