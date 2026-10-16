# Complete/v1 stale-use recovery at production admission

Status: T115 accepted at exact `4bb166efad5f3309d9a2a38db87fafd388844fb8`, 2026-10-11, after root and independent source/local/native audits. T116, T117, physical pruning and release readiness remain unaccepted. Checklist 45/128; T104 was separately accepted at `046b8d0`.

## Production behavior

The implementation through `e2662a8b6e5cd016d58e95b3cfd8fac4b58ff950` invokes `StalePackageGraphUseRecovery` before returning any enrolled non-Maintenance configured-root or install-path capability. Configured and path admission support the ordinary retained-owner and trusted-catalog branches. The ordinary path branch acquires and verifies the full sorted root/member union before recovery. Catalog recovery holds the complete catalog owner and recovers only selected projected roots. Existing Unenrolled early returns remain compatible; current incomplete/schema-2 refusal boundaries remain in place.

The private coordinator captures the exact verified Complete ledger before entering `LockedMemberLocations.WithValidatedRootAsync`. That existing callback serializes the retained context and verifies current native and semantic member evidence before and after the kernel call, including failure. It acquires no second admission or lock and does not reenter the member operation gate. Ownership remains available for reverse cleanup until capability transfer succeeds.

The existing [control-artifact recovery kernel](graph-use-control-recovery.md) performs bounded whole-namespace preflight, native exclusive liveness probes and fresh same-token mutation replay. It removes only exact stale graph-use control artifacts, preserves live sentinels, treats unknown evidence conservatively and never deletes package payloads. Maintenance remains descriptive: it observes stale protection as Unknown until an admitted recovery operation succeeds.

An actual unknown-artifact test exposed the same primary refusal being replayed by poisoned catalog-session disposal and aggregated with itself. Admission now filters only that exact exception instance from cleanup failures, recursively retaining distinct aggregate leaves and empty aggregates. Session poisoning and native verification are unchanged. Separate cleanup failures remain visible.

## Actual process and refusal proof

Three Release/net10 cases use the existing real AddNuplane HostIntegrated child, root/dependency assemblies and OS-owned sentinel. While the child is idle, a real Loading admission preserves exact use-control names, identities and bytes. After controlled normal exit or forced termination, Maintenance observes that same use as Stale; a fresh provider's Reconciliation admission reaps the stale pair. Maintenance then reports Known persistent protection. Complete ledger/member-state identities and bytes, exact nonempty loaded/offline-member Active/LKG closure and all package identities/payload digests remain unchanged from the post-load idle checkpoint. An unknown third artifact instead returns exact `PackageStoreAdmissionException`/`UnknownAuthority` and preserves the entire captured use namespace.

This real-child proof exercises configured-root trusted-catalog admission through actual AddNuplane registration. It is not a crash matrix for every configured/path × ordinary/catalog branch. Four additional native Store cases cover configured and path trusted-catalog refusal with successful cleanup and an injected native unlock failure. They require exactly one primary typed refusal, preserve the separate IOException, preserve the unknown artifact's identity/bytes and reacquire the full native lock set afterward.

Root compiled a causal omission of only the new coordinator-to-kernel call, retaining before/after context validation. Both real-child exit variants failed exactly because stale records remained (`Assert.Empty`), after successful child loading/exit and admission; neither failure was a child crash or timeout. The coordinator was restored byte-for-byte in `finally`: SHA-256 `0afe6998a4e6a50c2842d8a1ce2505c6f5d665b0f1a673dbc5b37542a1534b76`.

## Local verification

- Real-child positive3/3 and cleanup4/4, no failures/skips.
- Restored full Integration238/238, no failures/skips. All235 prior case names, outcomes and diagnostic outputs are unchanged; exactly3 new cases were added.
- Exact retained-Loading25/25 verifier passed against actual cases selected from the full Integration run. The manifest preserves all22 prior cases and adds the3 actual new TRX names.
- Full Store910 passed/0 failed/63 explicit platform skips/973 total. All967 outcomes in the earlier local Store baseline are unchanged; the two additional legacy symbolic-link cases were already present in published046, and this increment adds four cleanup cases.
- Full Runtime928 passed/0 failed/2 explicit platform skips/930 total.
- Core Release net8/net9/net10 builds: zero warnings/errors.
- The exact workflow member/install verifier passed on80 actual rows selected from the full Store run, including all4 exact cleanup cases and its existing platform-conditional outcomes. Required case IDs are captured from real TRX output.

Initial compilation caught a missing test import. Initial Store runs exposed missing capabilities in test decorators; these were corrected by forwarding the underlying native directory-name/publication, bounded enumeration and exact control-recovery operations. No production fallback or relaxed authority check was added. Original failed logs and TRX evidence are retained alongside passing restored runs. Native results for earlier046 qualify T104 only and are not credited to this new production delta.

## Remaining acceptance

Actual exact-head Windows/Linux/macOS x64/ARM64 qualification is accepted as recorded below. Full T114/T117 crash/read checkpoint coverage, T116 pending/incomplete/schema-2 integration, the unanswered public standalone-writer support contract and explicit maintenance/deletion application services remain separate requirements. No physical package deletion or public SaveAsync activation is implemented here. The original extraction, upstream releases and Foundation adoption remain independently shipped.

## Exact-head native acceptance

[Validate 38143776558](https://github.com/valence-works/nuplane/actions/runs/38143776558) completed successfully at `4bb166efad5f3309d9a2a38db87fafd388844fb8`: all seven jobs and all five native lanes (Windows x64, Linux x64/ARM64, macOS Intel/ARM64). Root and independent raw-log audits reconciled all 68 source pins. Every native lane passed strict retained Loading 25/25, graph-use recovery 26/26 and same-handle control recovery 11/11 without skips. The member/install verifier passed all 80 selected rows per lane, including four exact cleanup cases: Unix/macOS 77 passed plus three expected platform skips; Windows 76 passed plus four expected platform skips. Existing state-file, authority, writer, startup, catalog, restore and net8/net9/net10 serialization/group gates passed. Universal Darwin shim and Linux package asset verification passed.

The actual full six-suite CI run totaled 2378 passed, zero failed, 68 explicit platform skips, 2446 total. Together with the restored-source local runtime/protection proof and compiled caller-omission control above, this accepts T115. The bounded stale-use admission claim is released. The separate Complete/schema-1 pending-publication caller tests are not included in this source snapshot; their own native qualification remains pending. No complete T114/T116/T117 or physical deletion acceptance is implied.
