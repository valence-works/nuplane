# Participant catalog reads

This bounded increment implements active-catalog reads and descriptive loaded-package projection for the explicit coordinated composition. It does not activate maintenance execution or complete T100/T101.

## Behavior

`ActivePackageCatalog` retains its public ordinary constructor. The participant DI factory validates the selected provider, registry, serializer, admission and frozen roster before constructing the catalog. A read acquires non-recovering configured-root Maintenance ownership across the full catalog, requires matching enrolled ownership, and reads the configured member through the coordinated registry. Pending, corrupt or incomplete peers refuse without recovery or mutation. There is no ordinary fallback.

Loaded projection joins the exact current session, context and captured native install projection under the loader's existing context monitor. It enumerates candidates under that context's existing graph-use lease. It returns descriptive references without materializing assemblies. Expired or mismatched sessions refuse before scanning. Ordinary projection keeps its existing path.

## Local evidence and limits

Worker commit `5712ab88180674ac62333d331817bf44219f72a5` passed six focused Runtime cases and two Integration cases without skips. A compiled omission of the active-catalog factory validation caused the admission-only substitution regression to fail; the registry-only case continued to refuse through its existing admission factory. The production file was restored byte-for-byte and all six Runtime cases passed again.

Root integration strengthens the positive two-root case by checking returned install paths against the selected member, and extends the existing strict manifests from 57 to 63 coordinated Runtime cases and from 27 to 29 retained Loading cases. Every prior case remains required; selectors are unchanged. The combined local Runtime gate passed all 63 required cases, and both catalog Integration cases passed without skips.

The affected Loading tests passed 12 cases, including the eight required participant-composition cases. A test-only cleanup correction waits for the ordinary collectible context to be collected before deleting its fixture directory; it shares the existing bounded collection helper. These local results were followed by exact-head hosted qualification at `ff050346e4a20d3d17d087f738d2d9b4ec0c2a6b`: [Validate 38197763927](https://github.com/valence-works/nuplane/actions/runs/38197763927). Root and independent audits matched all 223 frozen source/test/manifest pins and accepted all seven jobs. Each of the five native lanes passed the strict 63-case coordinated Runtime gate, all 29 retained Loading cases, six participant Core cases and eight participant Loading cases. Windows passed Directory39, Manifest17 and native adapter/state-slot21+6. The full suite passed 2,549 tests with zero failures and 70 platform skips. [Acceptance and bounded claim release](https://github.com/valence-works/nuplane/issues/108#issuecomment-6117272164) retain the earlier failed/cancelled evidence and leave complete T095/T100/T101 acceptance open.

## Completed catalog boundary

The [admitted active snapshot](admitted-active-snapshot.md) and [public facade lifetime proof](facade-result-lifetime.md) close the two later catalog-boundary gaps. Exact head `6e447fcbad520248e65ce86276f6f75ebc5d3aa6` passed [Validate 38208470612](https://github.com/valence-works/nuplane/actions/runs/38208470612): seven successful jobs, all five native lanes strict Runtime81/retained Loading32, and 2,570 full-suite passes with zero failures and 70 platform skips. Root and independent audits matched all 241 source hashes and raw job logs. The existing participant active/projection cases remain required; no new T095-specific production gap was found. T095 is accepted with T090/T098/T094/T096.

The original extraction and release delivery is separate. Participant startup recovery is accepted under T079. Participant host-integrated admission, complete driver coverage, quarantine/deletion recovery and public maintenance remain open.
