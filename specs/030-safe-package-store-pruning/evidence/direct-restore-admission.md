# Scoped direct-restore admission

This bounded T084/T091 prerequisite protects public `NuplaneRestore.DescribeDesiredAsync` and `RequirePinnedVersions` preflight before desired-source reads. It does not accept either task or the complete T101/all-driver before-delete gate: built-in Directory and enabled DesiredManifest sources still need successful enrolled scoped implementations.

`RestoreComposition` acquires configured-root Restore admission before source callbacks and retains the exact Complete root/all-member owner across awaited aggregation. Positively Unenrolled calls retain legacy aggregation. Incomplete/unknown authority and unsupported enrolled participants refuse before callbacks. Describe releases its owner before a later reconciliation cycle obtains fresh admission; it adds no source snapshot persistence, acquisition, Loading or remote resolution.

The additive `IScopedDesiredStateAggregator` companion promises whole-source prevalidation, counted original-owner scoped reads, and typed refusal propagation. Existing `IDesiredStateAggregator` remains compatible. Built-in aggregation uses the same ordering/deduplication/error core for both modes, preserving original source instances, type-name/ToString ordering, configured precedence and ordinary source errors. A shared internal source helper also supplies reconciliation's existing borrowed-source routing. Enrolled legacy custom aggregators refuse before callbacks; typed refusals returned in a custom scoped result are checked before projecting error strings.

Worker `b245209` is integrated as `bb435ba`. Root and independent review found no actionable issues in this bounded slice. Independent review SHA-256 `9a1936e482ca1f2e27ff6e4c49a2dffba1f4ea6cc6046de668d2406d84334a54`; frozen worker manifest includes the exact source/patch, four actual TRXs and Core all-TFM build. Omitting whole-source validation causes the intended before-callback assertion to fail (supported source callback count 1 instead of 0); byte-exact helper restoration and the full focused class pass.

## Integrated local verification

| Gate | Actual result | Log SHA-256 |
|---|---|---|
| Native direct-restore class | 11 passed, no failures/skips | `25f38cfa4b925032fa38047caf76770a1bc7754fd72279e21cd739141a4a2692` |
| Core Release .NET8/9/10 | zero warnings/errors | `593220f530380fd0df8b16f6782482b8989fe01ac7327041ba29abd839e21df2` |
| Full Integration | 223 passed, no failures/skips | `69e9a8a6972f49767f8af5a8bc149f380216e66e03b1b46d41af3ca524e8644c` |
| Full Runtime | 878 passed, no failures/skips | `b0d2f465771f04dd5b3d31a0c4de164d6a810a41bca136714aadbfb08b0cffee` |

Core/full Integration/full Runtime used unchanged 963-input manifests, SHA-256 `185abb0593fc48117547bb8e6ab24f63f4d7b4cb766149a245a5a6b3c8126299`. The earlier focused class used manifest `74e553b7fda3c502b72ecb3842af9f5b8bdef4781ae8892043da7e37bad7cdd8`; the only later input changes are the exact eleven-name CI manifest and its workflow step. No production or test bytes changed between these gates. The manifest matches the actual focused TRX and all eleven cases also pass in the final full Integration run. Bash syntax passes; six accounting controls reject empty/missing/duplicate/extra/skipped/failed evidence.

The cases cover actual native Complete/Incomplete fixtures, the original root owner across an awaited source read and cancellation drain, pinned preflight, whole-source before-callback denial, unsupported custom aggregators, typed versus ordinary failures, custom returned refusal, original source ordering/identity, a real enrolled Directory source refused before its first stability callback, and real Unenrolled Directory compatibility. The Directory refusal case records its unsupported state at this checkpoint; it must become an actual scoped success regression when that driver is implemented. It is not evidence that enrolled Directory support is finished.

CI adds an exact eleven-case no-skip gate on all four native platforms. Actual hosted qualification for this direct-restore increment remains pending; earlier heads' recovery/content CI cannot qualify it. Enabled manifest support remains required separately, along with complete driver/startup/LKG qualification, multiroot snapshots/Loading lifetime, actual before-delete proof, public maintenance, deletion/recovery, stable upstream releases and Foundation adoption/e2e/main acceptance. No recursive package deletion or executor tests have started.
