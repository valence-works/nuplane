# Coordinated runtime admission increment

Date: 2026-10-09. This records runtime routing and compatibility proof; it is not production-driver, graph-lifetime or pruning acceptance.

## Behavior and boundaries

`AddNuplane` supplies physical-root admission to reconciliation and startup recovery. Reconciliation acquires the root and all enrolled member locks before its state lock, refresh, source callbacks and pipeline. A cycle creates adapters around the exact selected source, resolver, contributor, registry, failure recorder and observer objects. Awaited callbacks receive counted borrows of that owner; nested calls do not reacquire root ownership or use ambient state. Coordinated state reads and failure/source/unchanged-active publications bind the exact native member and selected serializer.

Enrolled participant preflight checks the whole selected source/contributor/observer set and relevant cleanup, diff, dry-run, lock-file and retry policies before callbacks. A built-in dry-run planner's nested diff engine must also declare path independence. Typed admission refusals propagate through retry, source fallback and apply handling. The built-in diff engine explicitly implements the value-only interface; it does not rely on a runtime type exception.

Custom registries or a mismatched/unsupported serializer use metadata-only classification. Positively Unenrolled existing and prospective roots preserve ordinary behavior. Complete/Incomplete authority refuses before the selected custom registry or payload reader is invoked. Raw configured locator components and one captured relative-path base are retained for native replay rather than normalized away.

The public startup-recovery constructor retains its signature. Manual composition first observes descriptive state through its selected registry, then classifies the complete active descriptor path list before target validation or observers. DI classifies the configured root before state access. Valid Unenrolled recovery remains supported. Enrolled recovery refuses until its exact recovery graph and lease path are implemented; this is not completed T079.

Two review corrections matter to the next production slice:

- `DesiredManifestPackageSource` keeps its original interface. An enabled manifest may itself reside in a package store, so declaring it path independent was false. A real valid manifest inside an enrolled install is refused before its first read. Disabled manifests remain excluded from cycles as before. Scoped manifest support remains required.
- A scoped custom resolver does not make the built-in dependency/runtime-asset reader scoped. Until that reader consumes native admission, the adapter refuses a returned nonblank install path while its borrow is still live, before built-in graph expansion. A real nuspec dependency regression covers this boundary. A separate path-free synthetic result reaches the later transition guard, proving refusal before `Changing` callbacks. Neither test supplies production graph-reader acceptance.

Default resolver/acquirer and capability contributor services still lack production scope consumption. Nonempty enrolled transitions, including no-op resolution returning installed packages, intentionally refuse in this intermediate implementation. Exact historical selection, coherent partly failed apply, graph-use publication and lifetime attachment must replace these refusals before feature delivery. The separate [native metadata/lifetime increment](native-metadata-and-lease-lifetime.md) records the reader and core lifetime mechanism; it does not complete these production drivers.

## Review and executable evidence

Root performed four review passes and fixed the manifest declaration, implicit graph read and stale guard-test coverage. A focused run caught a root simplification error: the built-in diff engine had not yet declared the marker used by the simplified preflight. The declaration was corrected after source inspection. Independent final review found no remaining blocking defect in the bounded 34 source/test deltas and additive workflow change.

The worker's console-only 31-case result is producer evidence, not a root gate log. Root focused v1 was stopped while still queued after the graph defect was found; it executed no tests. Focused v2 recorded 32 passes and one contract-declaration regression. Corrected v3 passed 33 cases without failures/skips. The final full suite below includes the subsequent test-only correction that separately reaches the `Changing` guard.

Three independently compiled mutation controls failed at their intended assertions:

| Removed protection | Actual regression | Log SHA-256 |
| --- | --- | --- |
| Reintroduced false manifest path-independence declaration | Expected admission refusal was absent | `1690fdc8cb09a416a9bf1b23c96d0de0c4ca06ab68c251930819e77cf463361c` |
| Removed pre-graph-read refusal | Expected admission refusal was absent | `cd7eec87afdb13b187c0279310791f487ad3c4019fcdc0a804000dcac6746506` |
| Removed guard before `Changing` | Callback count became one instead of zero | `21fbd43be60940293df7745a93322e90a78001e9cf3661b7dde97c2762e097e7` |

Each source was restored byte-for-byte before final gates. All three final commands preserve the same 841-input source/test/native/workflow manifest, SHA-256 `2877523beedb06e7a1984c488b6541a64f6f4e0588d47bac4f4455b4c396d168`.

| Final gate | Actual result | Log SHA-256 | TRX SHA-256 |
| --- | --- | --- | --- |
| Full Runtime | 854 passed; zero failures/skips; all 33 focused cases included | `7bcc73c71566fa023693e13331faa2199416e233817b7e06f100f8a86f5e0139` | `73f44ac33cd400841685814555dad0df97cb460d3cdf8bb97afd66a8b8100df2` |
| Full Store | 476 passed; zero failures; 31 explicit platform skips | `3f0256431cb9348f395c6608a9cb055fa2401cfecc63d778289a34cd079feca3` | `4bc308c351084dec082a5d12f55b1cd7df9e62661bb23755ef8547383ba5f365` |
| Core Release build | net8/net9/net10; zero warnings/errors | `9623439662c59a696097be82fbb22c4ea05a3641b818b95345aff2591b8da7e5` | Not applicable |

Source pins, runner inputs/results, full logs, TRX, mutation records and root review are retained under the owned `prune-admission-authorization-audit` artifact directory. The source/test pin manifest is `coordinated-runtime-root-reviewed-input-pins-v2.json`. Native platform skips are not credited as passes. No new whole-solution or separate Loading/Integration execution is claimed locally.

The workflow requires all 33 named runtime/startup cases on Linux x64, Linux ARM64, macOS ARM64 and Windows x64. It rejects missing, unexpected, duplicate, failed and skipped results. All thirteen embedded Python blocks in this increment compile; the new validator accepts the real focused TRX and rejects six missing/duplicate/skip/unexpected/theory/two-provider negative controls. Workflow SHA-256 at this increment: `c803c37cf5c18b02f541d1b196e7b13888eb1aa8bd4d33fb27bd27a07857bf07`.

Hosted [Validate 37988285668](https://github.com/valence-works/nuplane/actions/runs/37988285668) qualified exact implementation commit `0165ee71f7340d18c00e748ba4fceefc076d59ca`: all six jobs passed. The full Ubuntu solution run passed 1,826 cases with zero failures and 31 explicit platform skips (Runtime 854, Store 476, Loading 257, Integration 193, Directory 21 and NuGet 25). All four platform lanes passed the exact 33-case runtime/startup gate without skips. The separate 67-case native boundary gate passed 64 cases with three platform skips on Unix and 63 with four platform skips on Windows. All twelve framework/platform serialization lanes passed 97 cases; both owned Linux ext4 lanes passed eight cases without skips. The complete hosted log SHA-256 is `713255a77c1a1c9e14ff2a942db94183eab314a9f216a5ad766056682729341b`; parsed qualification SHA-256 is `ed11bb0ea4789b137cf476eaaaba13c72a2a9c94ee83de3a7fd84791ffa18c88`. These results qualify that commit, not subsequent metadata/lifetime changes.

## Acceptance limits

The real two-provider test proves owner retention across selected source callbacks and failure/source/unchanged-active state publication, including third-member and unsupported-participant refusal. Its source callback uses a test path read; it is not the mandatory default production-driver/native first-byte or overlapping-generation gate. No recursive-delete implementation may start from this evidence.

Checklist scope remains 30/128 accepted bounded artifacts. T068–T070/T079/T089/T100–T101 have partial routing/compatibility evidence; native production readers, every writer, historical transition/partial-apply coherence, immutable graph-use sentinels and actual context lifetimes, manual inventory/preview/confirmed execution and crash/platform proof remain required. Stable upstream packages and Foundation adoption/e2e/resulting-main verification remain required by the full program. No merge/release readiness or milestone completion is claimed.

## Full-catalog v1 runtime admission (2026-10-11)

Built-in `AddNuplane` configured-root and install-path admission now use one retained immutable trusted catalog for enrolled operations, following the [multiroot contract](../contracts/multiroot-protection.md). All root/member locks are retained before exact typed local/group recovery and acknowledged-state verification. Every recovery re-snapshots retained evidence; requested paths adopt only exact owned ledger outcomes while keeping their original native path and target observations. Positively Unenrolled targets return without acquiring unrelated catalog authority. Custom and manual composition retain their earlier compatibility paths.

This increment projects only Complete schema-1 acknowledged operation contexts. Stable schema-2/group catalog state refuses before desired-source or package callbacks, including when an independent v1 root is selected. Authoritative v2 guard/marker consumption and runtime projection remain unfinished.

Projected contexts reuse genuine transactions beneath the catalog borrow, with independent member maps and native root observations. All five asynchronous context entry points share a session gate before their local gate. Owned publication first refreshes the local context, then adopts the exact outcome into the catalog owner. Failed operations poison the session and preserve their original exception or cancellation; cleanup attempts every resource. Nonfinal projection disposal returns promptly, while the last projection verifies and releases the catalog after outstanding borrows drain.

### Local qualification

Source worker `f729375` was integrated as `df0470f`; root review and real-DI tests exposed a root-handle lifetime defect. Worker correction `fb360cb`, integrated as `10b09a3`, retains independent projection-owned root observations across owner refresh. The broader runtime gate then caught two preexisting provenance assertions whose specific errors were replaced during cleanup; worker `d69e9c7`, integrated as `4717ac6`, preserves the original operation failure. Root and independent final source/test review found no remaining actionable bounded defect.

| Exact Release/net10.0 gate on macOS native provider | Actual result | TRX SHA-256 |
| --- | --- | --- |
| Coordinated reconciliation admission plus public startup compatibility | 52 passed; zero failed/skipped | `39719648f9d89d97883a7faf25f23dc5efe316cb6c668a2da4e1883eeaf4e681` |
| Native group publication, catalog-owner verification and recovery | 48 passed; zero failed/skipped | `0dfc67aeecdfa065af7b9be2fb4eaff045666c6e942b837f1ece4e03252c23a7` |

The unchanged strict verifier accepted both complete manifests. They preserve every previous identity and add eleven runtime cases and four group/runtime-refusal cases. Coverage includes selected and unselected pending recovery, corrupt or omitted catalog peers before callbacks, two-provider retained ownership, repeated owned v1 publication with live peer projections, sticky poison after peer replay and post-publication refresh failure, drain/reopen, stable-v2 refusal and positive Unenrolled compatibility. The post-publication fault leaves a valid acknowledged selected-state update, refuses further peer use, reports close failure after draining, and permits a fresh verified admission.

An actual pre-implementation compiled control on `7b62cf4` ran ten of the new runtime scenarios: eight failed at their intended missing-fence/recovery/poison assertions, while two ordinary compatibility controls passed. Those same ten cases pass in the final gate. Failed intermediate and final TRXs remain separate; no failed run is credited as acceptance.

Root local qualification report SHA-256: `1f40b20ce5935322cfc6f950c904a8f39daa0e4808c2b5a54c29e69d68d13bc4`. The final production source matches the eight independent review hashes. The selected commands compile the affected projects; the Store test compilation reports the existing xUnit2031 warning in unchanged `RootMembershipRecordTests.cs`. The final worker net10 core build reports zero warnings/errors. Exact-head hosted all-framework and four-platform qualification is still required; earlier hosted runs do not qualify this new runtime source.

This is bounded local prerequisite acceptance only. The checklist remains **43/128**, with complete T026/T051/T064 unchecked. Ordinary standalone-writer compatibility/routing, authoritative v2 integration, remaining startup/LKG/restore/offline and graph-lifetime drivers, process-restart proof, public maintenance operations and real physical deletion remain required. No deletion or new package version is enabled. The [extraction releases and Foundation adoption have shipped separately](https://github.com/elsa-workflows/elsa-foundation/issues/2500#issuecomment-6104315745); the pruning follow-up still requires its own eventual release and downstream adoption.

### Incomplete-enrollment refusal correction

[Validate 38105799005](https://github.com/valence-works/nuplane/actions/runs/38105799005) failed at `e909278`: full Integration caught two existing restore/manifest tests returning `StateMismatch` instead of `IncompleteEnrollment`; each native platform lane stopped at the corresponding direct-restore refusal. The failure is retained as failed evidence, not qualification. Root reproduced both unchanged tests locally, with two failures and zero skips.

Worker `99b8a29`, integrated as `d890423`, restores `IncompleteEnrollment` for a nonpending Incomplete catalog root with no matching group descriptor. Common root/member checks, exact pending-state recovery, local group intent and descriptor-backed recovery retain precedence. Ambiguous or invalid group evidence still refuses, and incomplete roots remain inadmissible before source/native-read callbacks. Independent source review passed; no test, manifest or verifier changed.

| Corrected Release/net10.0 gate on macOS native provider | Actual result | TRX SHA-256 |
| --- | --- | --- |
| Full Integration, including both unchanged restore regressions | 232 passed; zero failed/skipped | `b66047ba31475c10f59984098636ba287f6964b61b54474e416997bfa6f679b1` |
| Exact coordinated runtime/startup manifest | 52 passed; zero failed/skipped | `0117836b9fc74dd44e51bb3e71ce25da5baa008641ade25cfccceb81f3416be6` |
| Exact native multiroot publication/catalog recovery manifest | 48 passed; zero failed/skipped | `09e1a9dba0515ab1d3c79d04dbf09e8428795026aa693054ee86ed2f57ec7b8a` |

The unchanged strict verifier accepted both manifests. Eighteen pinned source/test/gate inputs remained unchanged through these runs. Root qualification report SHA-256 is `409bc408f6165337a87a31cf1ec4b296c2adfadb1ae627a7605ccc077435cb6b`; the failed hosted log SHA-256 is `0a900e1d389f2095857d09d776eb7a8b513852963cb2b208abb1fb269aaef652`. Fresh exact-head hosted qualification remains required. This correction changes no whole-task acceptance or pruning/release readiness.

### Corrected exact-head hosted acceptance

[Validate 38106977884](https://github.com/valence-works/nuplane/actions/runs/38106977884) passed at published head `6946200927b428c15233956d0cf80aba036ffa8b`, a documentation-only descendant of corrected production source `d890423`. All six jobs succeeded. This supersedes the pending hosted qualification above; the earlier failed run remains failed evidence.

The full solution passed **2,326 tests**, with **zero failures** and **50 explicit platform skips**, across six assemblies. Restore, all-framework build and Darwin packaging verification succeeded. Windows x64, macOS, Linux x64 and Linux ARM64 each passed the exact **52 runtime/startup**, **48 native group/catalog** and **12 direct-restore** cases with zero failures/skips and the unchanged strict required-case verifier. Each platform also passed **128 serialization/digest** and **29 v2 encoding/compatibility** cases on each of net8.0, net9.0 and net10.0 without skips.

Root and independent review inspected fresh terminal run/job status, actual checkout SHAs, complete raw logs and unchanged source/test/workflow/manifest/verifier pins. Hosted TRX binaries were not separately downloaded; the exact unchanged workflow invokes the strict verifier and its actual output is recorded. Full-run log SHA-256: `f3456311c067201199c16346a36276d63ca7f847ae5963b2e65e7ca856c5f3f6`. Root hosted acceptance SHA-256: `565552b214982624889c246b6356871febc82dd16790e8df980bde658dc9b3d6`. Independent hosted acceptance SHA-256: `c8a45012c2dea89cb40edd8d7c749bb893d8fd3f6e9259cfb80c8ce42620fc73`. [Public acceptance and limits](https://github.com/valence-works/nuplane/pull/112#issuecomment-6104983270).

Only this bounded v1 catalog-admission/recovery prerequisite is accepted. Checklist remains **43/128**; whole T026/T051/T064 stay unchecked. Ordinary guarded-writer integration, authoritative v2 integration, remaining drivers, process/crash proof, actual pruning and its eventual release/Foundation adoption remain required. PR112 is not merge/release ready, and no deletion is enabled.

### Standalone writer ownership boundary (2026-10-11)

The internal standalone writer owns only the state-slot guard. It now refuses either schema-1 `ProtectionRecord` or schema-2 `ProtectionBundle`: incoming metadata before replay/native/payload I/O, persisted protected state under the acquired slot guard before backup/staging, and protection introduced by a custom codec before publication. Both committed-payload verification paths check the same ownership boundary. Coordinated writers and public `SaveAsync` routing are unchanged.

Three executed regression cases failed against the unchanged helper: incoming protection reached the injected native inspection failure; existing protection and codec-introduced protection were accepted without the required refusal. After correction, the full exact **20-case native writer manifest passed with zero failures/skips** on macOS ARM64, including the unchanged 17 prior cases. Tests verify prior bytes, absence of temporary/backup artifacts and guard reacquisition. The unchanged strict verifier accepted all 20 identities. Root source review and independent source/TRX review agree.

Baseline TRX SHA-256: `f5df29ea3638a84850f1d3f953857dcaa0f7fbd1509d3d5ea5c29e5037be92c6`; corrected TRX: `d78ac2059dc70bf823cfe7ec6accbda9e2f6d6fa09d8655aeb900e9a99c22711`. Root qualification: `c8b4a8b695e92136303a4c03155e0728e1d29d25f024725c8979ca234a83d9da`; independent source review: `ab69d532a40b259def1a55bbdc96c8a14b188410483cbd486b4c639941c49789`; independent result review: `7969cd74bcb771270998d7679831a710ab435fb3335afdba14b35cd317820cd8`. Fresh hosted qualification is required for this new source; the preceding `6946200` acceptance does not qualify it. Whole-pruning acceptance remains unchanged.
