# Native metadata reads and graph-use lifetime control

Date: 2026-10-09. This increment implements the native metadata reader and the core counted lifetime mechanism. It does not publish graph-use records, wire production Loading contexts, or authorize pruning.

## Native metadata access

The existing public metadata reader keeps its parameterless constructor and read signature. Its scoped companion reads `nuplane.json` relative to a retained native install-directory handle while a counted operation validation remains active. Root/all-member locks cannot close during this callback even when the original borrow is disposed. Complete root identity, enrollment epoch, ledger identity/digest and any admitted exact path union are checked before the read. Retained path and authority observations are replayed after the callback, including when it throws.

The leaf reader observes the exact native basename and parent name profile, refuses links, multiple hard links and foreign volumes, opens the single regular file without following links, and reads at most 64 KiB. It rechecks held/named file identity, length, link count and parent profile before parsing detached bytes. Missing and oversized metadata also require stable native observations. JSON/schema/loading/capability validation remains shared with the existing reader.

Unscoped reads use the same leaf reader only after positive Unenrolled classification. Native ancestry/authority evidence remains held through the complete callback; classification followed by an ordinary path-based open is not used. A distinct package-directory missing-suffix target preserves Missing for an entirely absent ordinary install path, without creating it or expanding a final package alias. Complete/Incomplete authority, escapes, reserved control-name ambiguity and unsupported Unicode/name profiles refuse. Relative paths use one captured base while preserving their original components. An absent suffix returns an explicit Missing status, never a null handle standing for Unknown.

The built-in capability contributor and Loading metadata adapter propagate admission refusals as typed exceptions before their missing/default branches. A refused capability read is not memoized, so a later allowed read in the same correlation is retried. These fixes do not make either caller a complete scoped production driver.

## Counted graph-use lifetime

The internal factory takes a core-validated immutable graph snapshot, exact fully qualified native install-path bindings for one physical root, and already-held native ownership. It copies and checks exact node coverage, duplicate identities/paths and root membership. Native binding and durable immutable graph-use publication are input prerequisites owned by T036; this factory neither establishes native authority nor opens package paths.

The owner/view control accepts only its exact lease and included paths. Closing rejects new pins, drains existing reads, and releases the supplied ownership once. Transferring ownership makes caller disposal harmless. Collectible lifetime targets are weak references; a passive core observer checks actual weak death without collection, unloading, user callbacks or pruning. Noncollectible owners remain process-retained until exit.

A process-static retention registry anchors transferred owners independently of an observer's service lifetime. Disposing an observer stops and drains its background work without releasing live targets. Dead targets can subsequently be reaped by another core observer; if none remains, ownership stays conservative until process exit. A failed native release retains diagnostic/control evidence and is not retried because its outcome and idempotency are unknown. This does not prove that the OS lock is still held after an underlying release has thrown.

The observer currently wakes once per second even with no collectible entries. Production registration, actual assembly-load-context attachment, partial Loading failures and process-exit record recovery remain separate work.

## Review and verification

Root review corrected two producer-test defects: owner disposal waited on a still-live lexical borrow, and the metadata read counter originally used the install-directory identity rather than the metadata-file identity. Native lock probes now dispose an unexpected successful acquisition even when an assertion fails. The live-target test holds its target across multiple observer polling cycles and uses `GC.KeepAlive`; collection helpers avoid rooting a target across test-side GC through `TryGetTarget` temporaries.

Preserved producer attempts are not final acceptance: v1 failed compilation; v2 was terminated after its test teardown deadlock was identified; v3 failed compile-time generic inference in a new throw-only callback; v4 reported 61 passes, four failures and three explicit macOS-inapplicable ext4 skips, but source changed while it ran. Its runner's zero exit status did not make that failing/source-unstable run a pass. Final v5 producer source was frozen and integrated by hash before root verification.

Root focused v1 passed 76 cases with zero failures and three explicit ext4-only skips on macOS. All 26 metadata/missing-suffix cases selected by the regular platform gate and all 11 lifetime cases passed. The subsequent live-target test strengthening requires final restored-source qualification below. Root's new capability-consumer refusal regression passed once without skips.

Final restored-source local qualification is complete. All four gates retain the same unchanged 847-input source manifest `dcd927aa254e74faeef28b77087890b30e07af791b20aee199ea924ea1740460`:

| Gate | Outcome | Log SHA-256 |
| --- | --- | --- |
| `native-reader-lease-store-final-v1` | 513 passed, 0 failed, 32 explicit platform skips | `c06441d6fa0a678291582d6fe33b64c755560fa58aa5e7df1c431ceb6e27c392` |
| `native-reader-lease-runtime-final-v1` | 855 passed, 0 failed, 0 explicit platform skips | `5b1b41e6a84dd078c274c6fef639557aa83efad9fb243cb38e639cc69ce30712` |
| `native-reader-lease-loading-final-v1` | 258 passed, 0 failed, 0 explicit platform skips | `f6533877bd025aaed1fdffe9bcedcc42b0446175818bf80277b230be251f03b1` |
| `native-reader-lease-loading-build-final-v1` | Release net8/net9/net10 build; zero warnings/errors | `991f93a4328da1740624b714bb5cea58ccc9e050d749a60a0a2323a4003356a1` |

Six compiled guard-removal controls each failed at the intended assertion, followed by byte-exact source restoration before the final gates:

| Removed guard | Intended observed failure | TRX SHA-256 |
| --- | --- | --- |
| Counted native metadata callback | Owner released while byte read was active | `f9f09e353545a1bd2e8342cc360e33730a54fb229526aa2e4dcbe590efccc0f6` |
| Exceptional authority replay | Original IOException escaped instead of typed authority refusal | `e36c40cad3f7a72fc5868cd53046819df011d1a85d7ae2d14422032db3bb9bb7` |
| Read-pin drain | Owner released before the pin drained | `5581f33a03b9ad9bf0a3898e1c6fea1645b2f0064928181fbfd6a7b68227f732` |
| Weak-target liveness | New live-target read pin refused after multiple observer cycles | `74fe70d1071919153680ede4abe88e1819877c30dbdff6b8f8ae3db537e9752f` |
| Capability refusal propagation | Expected typed refusal was absent | `f3c57581218d51b8e154c72538726e666dc292ce0759dab9cadc43913a4f37e0` |
| Loading refusal propagation | Expected typed refusal was absent | `0e03bc446fc0858be3f4fe32e35077e84ae74909a4d31c9029e31f5afcb7f76d` |

The first read-drain mutation used a compile-time constant and failed compilation with CS0162; it gives no behavioral credit. Its runtime-condition replacement compiled and reached the intended failing assertion. Both attempts are preserved.

The workflow adds exact-name/multiplicity/no-skip gates for 26 native metadata cases, 11 lifetime cases and both consumer regressions on all four platform lanes, plus the ninth owned-ext4 case on both Linux architectures. All sixteen embedded Python validators compile. Locally, the validators accept the actual final Store TRX subsets (26 and 11 cases) and both actual consumer regression TRXs; fourteen missing/duplicate/skipped/failed/unexpected/empty-result controls are rejected. These local validator checks are not hosted workflow execution.

The final independent read-only review and root diff review found no remaining actionable issue in this bounded increment; all nineteen reviewed source/test/workflow hashes match the final candidate.

Exact committed head `13dc3745ecb5406beee62448900f9af30544bfd9` passed all six jobs in
[Validate 37992882170](https://github.com/valence-works/nuplane/actions/runs/37992882170).
Root parsed the complete logs: the full Ubuntu solution passed 1,865 cases, failed zero and skipped
32 explicitly platform-inapplicable cases (Runtime 855, Store 513, Loading 258, Integration 193,
Directory 21, NuGet 25). Each Windows x64, macOS ARM64 and Linux x64/ARM64 lane passed all 26 metadata,
11 lifetime, two consumer and 33 runtime/startup gate cases without skips. All twelve serialization
framework/platform executions passed 97 cases each without skips; both owned-ext4 lanes passed nine
cases each without skips. Complete log SHA-256 is
`38145974a9f7298067887372a3dc390f6cdef9f4b091ee3944ff1730f07777b7`; parsed qualification SHA-256 is
`383f658409aab961d95eb0b82ba35a1836ec6909b7732e0b029407ede6e46d7a`.
This hosted run qualifies this committed increment only, including the bounded T037 owner/control
and T080 metadata-reader implementations. It does not qualify subsequent root-access or native
enumeration work.

## Acceptance limits

No native graph-use record/provider, complete scoped contributor/advisor/resolver, actual Loading context lifetime, two-composition overlapping-generation first-read gate, manual deletion, stable release or Foundation adoption is proved here. T036, T086–T099 and the mandatory non-destructive integration gate remain required. T037 and T080 are accepted only for their defined owner/control and reader scope; native binding/provider and Loading integration remain required elsewhere. T085 stays open for complete observer lifecycle/registration integration. The bounded checklist count is 32/128, not a program completion percentage.
