# Configured maintenance root labels

This implements the existing T027/T038–T040/T048 slice without activating native admission, enrollment, inspection endpoints or pruning. `PackageStoreMaintenanceOptions.RootLabel` is the sole new option, bound from `PackageStoreMaintenance`, with default `default`.

The internal resolver snapshots a case-insensitive alias map over the exact immutable trusted-catalog descriptors. RootLabel replaces only the maintenance alias of the catalog's reserved default entry; extra roots retain their labels. The default path still comes from the catalog's final FeedResolutionOptions. Unknown labels and unconfigured paths do not resolve. Later option mutation cannot retarget the snapshot. This selection is configuration data, not native filesystem authority.

Startup validation rejects null/blank/padded labels and collisions with final additional-root definitions. Existing feed validation still rejects a provided blank install root. Registration retains custom validators and adds its own concrete/factory alias once across repeated AddNuplane calls. Neither validation nor lookup resolves native filesystem services.

## Local verification and review

- Focused tests: 12 passed, zero failures/skips, with the unchanged strict exact-case verifier. Four parameterized invalid-label cases and eight facts cover configuration binding/overrides, catalog identity, alias refusal, immutable lookup, repeated registrations, retained custom validation and native-service nonactivation.
- Full Runtime: 917 passed, zero failed, two explicitly Windows-only skips, 919 total. All 12 new identities and all five existing catalog identities appeared once and passed in the original full TRX.
- Core Release build: net8.0/net9.0/net10.0, zero warnings/errors.
- Compiled causal control: omitting only ValidateOnStart caused all four invalid-label cases to fail because no validation exception was thrown. Production bytes were restored and the final focused gate rerun. No control failure counts as a passing gate.
- First focused attempt: 11 passed/one failed because the blank-feed-root test assumed one exception; existing feed/capability startup validation aggregates two errors. The test now checks every underlying exact feed-options validation error. Production code did not change for that correction.
- Root self-review stopped clean after two iterations; independent read-only review found no actionable issue. Copilot/Greptile were not requested. Workflow YAML and the added Bash step parse successfully.

Local logs, original TRXs and SHA-256 pins are retained under the program artifact directory `extraction-scope-recovery-v1/configured-root-label-v1`. CI adds a separate mandatory 12-case gate to the existing four native lanes without changing earlier manifests or verifiers.

## Hosted qualification

Exact source head `5586a3afa4418bbaa1428277286250c73d642f42` passed all six jobs in [Validate38086130466](https://github.com/valence-works/nuplane/actions/runs/38086130466). Root inspected each completed raw native job log: Windows x64, macOS, Linux x64 and Linux ARM64 each passed all 12 new cases with zero failures/skips and the unchanged strict exact-case verifier. Source/test/workflow hashes still match local qualification. No hosted TRX was uploaded/downloaded; acceptance uses actual successful verifier output and job logs.

The full build/test job passed six assemblies: **2,301 passed / zero failed / 50 explicit platform skips / 2,351 total**. Runtime contributed 917 passed/two Windows-only skips; Store contributed 847 passed/48 platform skips; Directory 21, Loading 259, NuGet 25 and Integration 232 passed without skips. Root verified all six summaries and 50 unique skipped identities. The full-suite log SHA-256 is `5d1e9d8fec7e75e45b145f1b7876f2140718c6b9ba732e640a99e23947920466`.

Final hosted qualification SHA-256 `612976439b0dd7a11a66a16753ccef89a0d0ee6576cfd90bf5d15b026c7f1ab0` records the four platform log hashes, exact source, terminal metadata and full-suite accounting. T027/T038–T040/T048 are accepted; the checklist is now **43/128**. This does not accept other partial tasks or make PR112 merge/release ready.

Current inspection/admission is still default-root-only. This slice does not prove additional-root operations, complete enrollment, public maintenance APIs or physical deletion. At the configured-label checkpoint, stable releases and Foundation adoption were still open. The owner later approved [extraction-first delivery](https://github.com/valence-works/nuplane/issues/108#issuecomment-6102352983), and [the extraction release/adoption closeout](https://github.com/elsa-workflows/elsa-foundation/issues/2500#issuecomment-6104120978) is now merged. The full program remains open for safe pruning and its later release/adoption; this bounded label qualification grants no pruning capability.
