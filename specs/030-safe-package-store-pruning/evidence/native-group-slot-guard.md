# Internal group publication slot guard

This increment connects the existing native slot guard and permanent group marker to the internal group publication/recovery kernels, including retained-catalog adoption. It has no runtime, DI, producer, CLI or deletion activation. The checklist remains 38/128; no complete pruning task is accepted.

The full root/member union is acquired before the shared-slot guard. The exact marker is established before the first state payload read or Intent, replayed around callbacks, ledger operations, awaited payload reads, state replacement and artifact removal, and retained through final outcomes. Disposal releases the slot guard before participant ownership. Marker-only interruption recovers the exact descriptor-bound Prior without clearing or rebinding the marker. Refusal after an acknowledgement preserves the actual committed prefix rather than claiming rollback.

## Local verification

Root ran the final Store suite and Core build serially on macOS through the shared build wrapper, against unchanged executable inputs:

| Gate | Actual result |
|---|---|
| Store Release/net10 full suite | 832 passed, zero failed, 44 explicit platform skips; 876 total |
| Required native group/catalog cases within that unmodified full-suite TRX | All 34 identities appeared exactly once and passed without skips |
| Core Release build | net8/net9/net10; zero warnings/errors |

The earlier focused snapshot passed the strict 34-case verifier. The final candidate removes a redundant helper and uses equivalent xUnit predicate overloads; its full-suite result above executes those exact final bytes. The strict verifier requires an exact result set and therefore rejects a full-suite TRX containing additional tests; root separately checked the required subset without changing the full TRX or weakening the verifier.

A compiled causal control omitted only marker replay after the payload-read callback. The regression failed at its intended assertion: expected serializer reads zero, actual one. Exact production bytes were restored, then rebuilt for the passing final full suite. Initial pre-build cancellation and a subsequent accessibility compilation failure remain preserved; neither executed a test suite. The only remaining Store compilation warning is the existing xUnit2031 warning in unchanged `RootMembershipRecordTests.cs`.

Frozen artifact hashes:

- Executable-input manifest (1,006 files): `e4dabd36a4f65fc6967347289cc56ed3c98ceb0845411ffad62cf1975aaa4234`.
- Final qualification: `e39364380b9d4211436fcf772c0f5044f88871bdecaf3d0ad4adef7c135db67b`.
- Full Store TRX: `e4f843d6d14685f0ea5eef9f8faddd65c84f8d758cf19aeda8c0165e45c4c8d6`.
- Core build log: `44a7d432a8d761c49871ebe80b91791feed356686722713222c17af75ef5a0f1`.
- Independent final source review: `017446b5b8949117d79b48883e051cbe6e5550583e6f756b390d3f1fbf54ca34`.

Root and independent review found no remaining actionable issue in this bounded candidate.

## Integrated-head hosted verification

[Validate 38068400371](https://github.com/valence-works/nuplane/actions/runs/38068400371) tests integrated head `71d452c0b29d6a6153182976c5f8feec374e9d00`. Its full build/test job passed all six test assemblies: 2,272 passed, zero failed, 48 explicit platform skips; 2,320 total. Store accounts for 830 passed and 46 skips, Runtime for 905 passed and two skips. The remaining assemblies passed without skips: Directory 21, Loading 259, NuGet 25 and Integration 232. The build retained one existing xUnit2031 warning in `RootMembershipRecordTests.cs:79` and reported zero errors.

All six jobs completed successfully, including Ubuntu x64, Ubuntu ARM64, macOS, Windows and the universal Darwin shim build. On each of the four platform lanes, the focused native group/catalog step executed 34 cases with zero failures/skips and its unchanged strict verifier confirmed the exact required identities. This qualifies the internal increment at the integrated head; it does not accept the complete pruning feature.

Root retained the unmodified completed-job logs and checked the group result and strict-verifier output within the corresponding step, separately from other 34-case gates. Final hosted qualification SHA-256: `f85e8c9f4215383e2e14c51699e48af795db9426f3c7a8a956aeef0c604d09fc`. This workflow does not upload those TRX files; hosted evidence consists of exact-head job state and execution/verifier logs, not a claim that root downloaded and re-parsed the hosted TRX.

## Remaining boundary

Ordinary standalone-writer compatibility, all authoritative runtime/startup/LKG/restore/offline paths, actual process-kill recovery through a Complete/schema-1 peer, public maintenance operations and physical pruning/deletion remain unfinished. No stable release or final Foundation adoption is proved here.
