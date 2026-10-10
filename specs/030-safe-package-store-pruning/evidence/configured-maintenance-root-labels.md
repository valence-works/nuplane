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

Local logs, original TRXs and SHA-256 pins are retained under the program artifact directory `extraction-scope-recovery-v1/configured-root-label-v1`. CI adds a separate mandatory 12-case gate to the existing four native lanes without changing earlier manifests or verifiers. Exact-head hosted qualification is pending; task checkboxes remain open until that qualification is recorded.

Current inspection/admission is still default-root-only. This slice does not prove additional-root operations, complete enrollment, public maintenance APIs or physical deletion. The full goal, stable releases and Foundation adoption remain open; the proposed extraction/pruning release split is unchanged.
