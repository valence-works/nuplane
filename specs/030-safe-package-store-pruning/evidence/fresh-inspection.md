# Fresh all-member maintenance inspection

The internal inspection kernel now connects configured-root admission, every acknowledged member's protection, native live-use inspection, bounded inventory and the pure retention planner. This is read-only orchestration; it does not complete public configured-label operations, legacy enrollment preview, stale-use recovery or the mandatory before-delete gate.

## Ownership and observations

`PackageStoreInspectionService.InspectAsync` validates the optional nonnegative retention budget before acquiring its own Maintenance admission. For an enrolled root it retains that operation owner and its original borrow throughout the pass. It requires the exact non-pending Complete ledger, root, positive epoch and full member union. It reads and verifies each acknowledged member in stable order, preserving full Active and recoverable LKG install identities, revisions, state-body digests and protection digests.

Native graph-use inspection occurs inside one validated-root callback. Member reads and inventory occur outside that callback because the operation context serializes these calls on a non-reentrant gate. The filesystem provider must be the admitted provider. The service checks root/epoch on native use and inventory results and revalidates the captured ledger digest after each phase and before returning.

Live records protect their complete exact graphs. Stale records remain visible and make aggregate protection Unknown while retaining persistent positive protection facts; otherwise unprotected rows refuse. This conservative interim behavior requires future stale-use cleanup before execution. An unenrolled root reports EnrollmentRequired/Unknown without a fabricated epoch, inventory or plan. Incomplete membership refuses before inventory. An incomplete inventory cannot yield eligible unprotected rows.

Returned members, graph-use descriptions, inventory and plans are immutable detached observations. They contain no native handles or deletion authority. Missing retention keeps all; explicit zero makes only proven unprotected completed installs eligible. The service reads no package payload bytes and mutates no control or package files.

## Review and root verification

The six integrated source/test files match reviewed worker commit `b1b884764f2d75114bf0cf5f0f75e28bd86033d8`. Root retains the corrected inventory implementation from `5942d5f`; it did not copy the worker's older dependency checkpoint. Root review and independent source/evidence review found no actionable finding in the six-file slice. Independent review SHA-256: `f4ceb418ba73a5ae8b1d61580e6e496326c5ea9b7bb65fab72b4e902ed28b920`. The reviewer did not execute tests.

| Root gate | Result | Log SHA-256 |
|---|---|---|
| Integrated inspection suite | 10 passed / 0 failed / 0 skipped | `e4542a0dcea0448d54a396ebc8e3564b93574063544b81cf4eb362f18ddb3612` |
| Complete Store suite | 687 passed / 0 failed / 41 platform skips | `712efb2b6ede83e65ff3e48a00c2e2cdd760553a8a1792e83c9031d495dedcc0` |
| Combined inventory/planner/inspection | 41 passed / 0 failed / 0 skipped | `c71e4b688b69c268a3f3ec03132b9bb9b050cd40d4a29eefbdf2f4a48ecc13ba` |
| Core Release .NET8/9/10 | zero warnings/errors | `9d51a0599f112b933b6429425ac191f600c9d0094733d0dd84d5be5cefd5f9e2` |

The first two gates use input manifest `34af4f2d03e6be72ffa6214d75cbefeb9325d49667d87784c8364ab1465a7a40`; the last two use `335f998a89727a6ed45c32af5b2a9511d83e26c7b73a11265811594ed20801e0` (943 inputs each). Only the workflow and its two exact-case manifests changed between those groups; every production/test input is byte-identical. The existing xUnit2031 warning in RootMembershipRecordTests remains outside this slice; production builds have none.

The combined hosted gate requires exactly 41 Unix or 40 Windows cases. Root verified all 41 actual macOS results, structural accounting for the Windows subset, twelve malformed-result rejection controls, eighteen existing embedded Python blocks and the changed Bash step. Accounting report SHA-256: `62e58ae47859fe922dfe9ecd24c06cb67a22ed925fb20b6d1e0d986b611138e4`. Structural accounting alone did not qualify Windows behavior. The exact-head hosted execution below now qualifies those cases on Windows as well as Unix.

Worker causal evidence omitted the service's persistent Active/LKG protection inputs and failed the zero-budget test at the protected row (`Expected Retained / Actual Eligible`). The exact service bytes were restored and the same case passed. Failed-control TRX SHA-256: `bce77f9e5b791c126c398291e7bb4dc9f800baa8cd0e1f6161f81d9f3d53b6d7`; restored-source SHA-256: `fc54d42df53c6158569b24005768266ef6cf5f277b185336ea58a051d121d95a`. Root hash-verified the evidence and executed the combined gates above.

## Hosted inspection qualification

[CI run 38026139136](https://github.com/valence-works/nuplane/actions/runs/38026139136) completed successfully on exact published head `abe484943a096e3fd0a0e32590322fdfb3a30851`. All six jobs succeeded, including native macOS ARM64, Windows x64 NTFS, Linux x64 and Linux ARM64 lanes. Each platform executed the required combined inventory/planner/inspection cases: 41 on Unix and 40 on Windows, with no skips in those required selections. Both Linux owned ext4 casefold lanes passed all 11 cases without skips.

The full solution reported **2,078 passed, 0 failed, 41 platform skips**. Root captured terminal provider state and the complete hosted log, then checked actual timestamped result messages against all 28 required case-count qualifications, all six full-solution assemblies and both ext4 lanes. Qualified summary SHA-256: `699719c503673efb9fa72a972564e2516d176e121f9e2066af0762f944ddea96`; full hosted log SHA-256: `5933c0ff147c041e99cc1fc2971e475fbfa14292a5705299f88bba34e5760ec8`.

This run qualifies the published inspection increment. It contains neither the subsequent native control-recovery candidate nor the actual maintenance/Loading overlap proof. It does not accept public configured-label operations, stale-use recovery, recursive package deletion or full driver/before-delete acceptance. No additional checklist task is accepted; the count remains 37/128.

## Remaining acceptance

T051 remains open for public configured-label orchestration and legacy enrollment preview. The actual two-composition maintenance/before-read integration proof is independently owned and has not passed review. Complete runtime-driver acceptance, stale-use cleanup, physical package deletion, crash recovery and stable upstream/downstream delivery remain required. This local inspection integration accepted no additional checklist tasks. Subsequent hosted qualification of the preceding inventory/planner head accepts T049/T056, bringing the checklist to 37/128; see [inventory platform evidence](inventory-and-retention.md#hosted-inventory-acceptance). #108 and PR112 remain open and not merge/release ready.

Root review of the separate stale-use proposal also rejected unlocking a Windows file after successful delete disposition. [Microsoft's disposition contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-_file_disposition_information) requires closing that handle next. The native companion slice must retain the lock through disposition and close, then verify absence; it cannot use a close-first/reopen removal. Its implementation and actual platform qualification remain pending. This control-artifact work does not authorize recursive package deletion.
