# Actual process partial-execution recovery proof

This records bounded T118 implementation and local qualification under Nuplane #108 / PR112. It does not accept the complete pruning feature, public maintenance, historical recoverable-only graphs, or OSR-006. The checklist remains74/128 until required platform and broader acceptance evidence is reviewed.

## Implementation and independent review

Worker `40fe439e9e60b41867e097abb16958562bf1de13` adds the dedicated `PackageStorePartialExecutionRecoveryTests` class, the existing test-host execution command and participant launcher, and minimal shared fixture access. Root integrated it as `f71550f`, added the strict native CI gate/manifest at `5309d84`, and addressed the independent P2 preservation-assertion finding at tested code `4eaf51340d71bca5b4bfdc61a3ee6b270bdb14b5`. Independent re-review found the source finding addressed with no new findings. Existing retained-Loading51 selection is unchanged.

The child builds the real registered participant composition and invokes the internal fresh-plan executor. Requests carry operation IDs and the parent checks the actual child PID; every interruption awaits termination before a new child/request explicitly resumes. Parent observations use native journal discovery, exact directory/file identities, manifest entries and payload digests. No parallel ledger, simulated executor or report database is introduced.

## Covered scenarios

- Hard process termination at all seven physical checkpoints: Quarantined publication, Deleting publication, first payload removal, Finalizing publication, marker removal, quarantine-handle close and quarantine-root removal. A fresh operation reports verified Deleted or AlreadyAbsent facts against the actual namespace.
- Hard termination after Prepared publication, followed by explicit fresh-process recovery of the exact candidate.
- Cancellation during the second candidate: preserve the first completed deletion, report the second partial removal, leave the later candidate unattempted with its pre-run native identity and bytes unchanged, then resume honestly.
- Cancellation before admission: preserve every fixture install and state and create no deletion journal.
- A valid coordinated protection update after preview: freshly retain the newly protected version and delete only the freshly eligible versions.
- Tampered member-state body and an unknown quarantine entry: refuse, preserve all relevant package/state evidence, and preserve journal identity/bytes plus the entire quarantine namespace and bounded per-file payload digests.

Protected installs are independently derived from persisted Active and Recoverable closures, then checked against planner rows/reasons and compared by native identity and payload bytes across execution/restart. Ordinary admission is observed before explicit recovery and must not resume physical deletion.

## Local actual results

Worker focused attempt007 passed13/13 without skips or warnings. Attempts001–006 and their original diagnostics/results remain preserved, including compile-only and interim assertion failures.

Root corrected Release/net10 focused qualification passed13/13 without failures or skips. The final affected Release/net10 Integration run on `4eaf513` passed **284/0/0**, preserving every prior271 actual test name exactly once and adding exactly the13 required recovery rows. Every retained-Loading51 case also occurs exactly once and passes. The final TRX SHA-256 is `8071e2193cfe5726eaf1bffee7ad934104cfe31955b22912a7647c73bd144dcd`. The prior root full-suite attempt was deliberately terminated for review corrections and is preserved as unaccepted, not counted as a test failure or pass.

Commands use the normal PATH build-slot wrapper:

```bash
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj \
  --configuration Release --framework net10.0 --no-restore \
  --filter FullyQualifiedName~PackageStorePartialExecutionRecoveryTests \
  --blame-hang-timeout 3m --logger 'trx;LogFileName=root-corrected-focused.trx' \
  --results-directory <owned-results>/root-corrected-focused --verbosity minimal

dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj \
  --configuration Release --framework net10.0 --no-build --no-restore \
  --blame-hang-timeout 3m --logger 'trx;LogFileName=root-final-integration.trx' \
  --results-directory <owned-results>/root-final-integration-02 --verbosity minimal
```

Actual commands, logs, TRX, source pins, root and independent reviews are retained under the canonical delivery artifact directory `catalog-runtime-integration-v1/partial-execution-recovery/`. The shared store implementation is unchanged in this slice; earlier Store/native execution regression evidence remains separate.

## Scope and platform limits

Local host was Darwin arm64. The workflow now requires all13 names exactly once and Passed on each of the five native OS lanes, preserving the previous gates/timeouts. Hosted qualification of this candidate is pending. The preceding head `47554cf`, [Validate38251241843](https://github.com/valence-works/nuplane/actions/runs/38251241843), failed on Windows after14/51 retained-Loading cases and a three-minute inactivity abort; all other six jobs passed. Its emitted dump was not uploaded, so the blocking phase/cause remains unproven. The next candidate retains owned retained-loading diagnostics on failure. No Windows/compiler or full platform acceptance is inferred from local success.

The offline member's graph is valid **ActiveAndRecoverable**, distinct from the online member and absent from live-use records. Current and released0.0.11 startup semantics require selected Active/LKG/descriptor equality. This proof does not manufacture an inadmissible active-v2/recoverable-v1 state and does not establish historical LKG-only support or full OSR-006 acceptance.

Public enrollment/migration/manual APIs, operator guidance, final platform qualification, release and downstream adoption remain required. No public pruning activation or new policy extension framework is added.

Test source SHA-256: `5a5e38de1305359927fdc0bf6e5548ff6ba293910fbd66a0bd343f8b43b0e6d5`.
