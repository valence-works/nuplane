# Internal fresh execution-planning boundary

The internal `PackageStoreExecutionService.PlanAndRunAsync` acquires the configured catalog through the inspection service's shared ownership/cleanup helper. It keeps one native catalog owner, borrow and exact operation alive through selected-root epoch confirmation, full-catalog protection verification, graph-use observation, native inventory, retention replanning, replay and awaited candidate work. Ordinary inspection retains its existing wrapper verifier and projection body. No preview identifier or previously returned inspection is an execution input.

The explicit operation-aware verifier validates the exact current token and registry before reading state. Foreign, forged and expired tokens cannot authorize verification or end a real active operation. An already-held operation may drain after borrow disposal is requested. The execution boundary requires positive enrollment, exact expected epoch and explicit nonnegative retention; it refuses incomplete inventory, unknown protection or unresolved planning evidence before candidate work. Candidate failures and cancellation preserve the existing exception/cleanup behavior. An escaped context or native root handle expires when ownership closes.

## Local proof

- Nuplane source build passes on .NET 8/9/10 with zero warnings or errors.
- Focused inspection/verifier/planning regression: 45 passed, zero failures or skips.
- Native full group/catalog gate: 71 exact required cases, preserving all previous 56 and adding 15 token/planning cases. The final run also verifies the captured native root handle refuses after teardown.
- Full Store regression: 992 passed, zero failures, 63 explicit platform skips, 1,055 total. This includes the strengthened independent third-root lock test. The final native-handle-expiry assertion was added afterward and checked in the final 71-case gate; production source is unchanged.
- Tests check the entire configured lock union at state reads, native inventory, planning and candidate work, including an independent Complete/v1 third root alongside the shared v2 roots. Other cases cover changed acknowledged state and newly completed inventory after preview, epoch/label/unenrolled refusal, corrupt/pending peer, incomplete inventory, cancellation and candidate failure.
- A compiled omission of both epoch-confirmation comparisons makes the actual changed-epoch regression fail because candidate work is reached. Production was restored byte-for-byte before the passing native regression.

Root reviewed the delegated exact-token change and integrated it as `e66d4bd` from worker `733edd1`. Independent source review found no correctness blocker in this bounded internal slice. Private qualification artifacts retain raw TRX, omission/restoration hashes and reviewed source hashes.

## Exact-head hosted qualification

Exact `2410e5186a90a509cdd6afe5cc38bec9306cc1c9` passed [Validate 38168539490](https://github.com/valence-works/nuplane/actions/runs/38168539490). Root and independent audits verified all seven jobs, five native lanes and 137 frozen source pins. Each native lane passed all 71 required group/catalog cases without skips. Actual full build-test totals were 2,468 passed, zero failed, 68 explicit platform skips, 2,536 total. This qualifies the internal planning increment; it does not accept the complete executor or public activation.

## Remaining integration and acceptance

The generic internal callback and retained root handle are not candidate mutation authority. T107 must bind one exact current Eligible row to held native ancestry/parent/install/marker evidence under this same operation and revalidate immediately before quarantine/removal. Quarantine recovery also needs the verified journal/inventory join in the [recovery contract](../contracts/quarantine-recovery.md); ordinary inventory classification of a quarantine name is insufficient. The protection-change-to-Retained deletion integration test remains required.

No public execution service, DI registration, schema-2 activation, quarantine transition or recursive deletion is added. The [answered owner support policy](pruning-support-policy.md) preserves ordinary compatibility and restricts pruning to verified environments and participating clients. Public confirmation/routing still requires implementation and qualification. T106/T107/T111/T112 remain open until complete integration and exact-head native qualification. Original extraction/releases/Foundation adoption remain shipped separately.
