# Held-parent publication primitives and stream codec

This T021/T034 prerequisite adds a separate internal `IPhysicalStorePublicationFileSystem` companion, implemented by the Unix and Windows adapters. It atomically publishes a staged single-link regular control file into a same-parent canonical slot, or removes one exactly identified transaction-control artifact. Shared checks bind parent identity, exact native basename/profile, source/prior identities and post-publication identity/absence. Native absent publication must refuse replacement; unsupported operations have no path-based fallback. Each irreversible namespace operation executes once; an error can follow a completed change and must be reconciled from actual evidence.

The caller must already hold exclusive root/member coordination ownership, close handles that prevent delete sharing, flush/verify staged and backup payloads, and retain pending evidence until publication is acknowledged. Expected-identity observations are **not** an atomic compare-and-swap against arbitrary filesystem writers. This increment supplies no root/member locks, backup orchestration, persisted ledger, pending recovery, admission, acknowledgement, recursive/package deletion or power-loss durability. **T033 and T034 remain open**, as do full multi-state protection and the real two-composition pruning proof.

`IPackageProtectionStatePayloadSerializer` is an optional public companion for already-open caller-owned bounded streams. The default serializer shares its strict DTO codec and normalization with legacy path APIs, leaves streams open and forwards cancellation. A capability declaration is not publication proof. T031 remains partial until the coordinated publisher independently reopens actual saved content and refuses dropped protection/digest mismatch; a path-only custom serializer cannot be an enrolled-store fallback.

## Native boundary and review

Unix uses descriptor-relative native rename/no-replace/unlink. Windows uses a DELETE-capable staged handle, native held-RootDirectory rename and single-file disposition, then closes that handle before ordinary no-delete-sharing canonical reopens. Existing adapter opens retain their original sharing defaults; publication-local opens explicitly share DELETE. Native contract sources: [Linux rename](https://man7.org/linux/man-pages/man2/rename.2.html), [Microsoft FILE_RENAME_INFORMATION](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information), [NtSetInformationFile](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntsetinformationfile), and [Apple rename declarations](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/stdio.h). These references inform source design; platform tests remain separate execution evidence.

Root and two independent source reviews found no remaining blocker. Root verified seventeen final reviewed source/test/workflow hashes. Root removed uncertain-error retry loops, corrected a missing test import and an owned symlink fixture-cleanup mistake, and strengthened tests whose setup could mask source/destination link checks or no-replace semantics. The direct native occupied-target tests require EEXIST/STATUS_OBJECT_NAME_COLLISION; Windows handles are scoped so a sharing violation cannot satisfy them. Source review reports are retained separately from execution evidence.

## Local verification

Final core build passed net8/net9/net10 with zero warnings/errors. The full Store suite passed **269**, failed **0**, with **23** explicit Windows/ext4-casefold skips. All eight ordinary Unix publication facts and seven payload cases ran locally on macOS ARM64. The focused serialization/payload/digest suite passed **64**, failed **0**, skipped **0**, on each of net8/net9/net10. Existing legacy atomic writer/state tests remain green.

Native cases cover existing and absent publication, unchanged stable slot with changed file identity, prior/next bytes, occupied no-replace preservation, stale identities, independently arranged source/destination symbolic and hard links, exact artifact cleanup, foreign/closed parent handles, and platform-specific held-parent behavior. The profile-aware case test distinguishes a real case-insensitive alias from a distinct absent case-sensitive name. Hosted Linux qualification additionally requires the owned ext4 casefold publication and identity tests to run without skips. Windows/native Linux and casefold execution for this increment are **pending**; local skips do not qualify them.

Removing Darwin's native no-replace flag caused exactly one intended missing-exception failure in the direct native test. Removing canonical basename refusal caused exactly one intended missing-exception failure on local case-insensitive APFS. Both controls had zero passes/skips in their selected tests. Root restored original production bytes after each control before the final green gates. No concurrent-reader, process-crash, backup restoration or full state/ledger publication proof is claimed here.

| Final evidence | SHA-256 |
|---|---|
| Independent production/workflow review | `29d19f6c9dfd7662c4d7c70e33121a88caae2434c9029471069771566df86761` |
| Independent tests/payload review | `0e32245992e91d2863cb8890eb34d582f2556dad4597c1db65c4bec588def328` |
| Core all-TFM build log | `c4c97da67b7de14756fc75af7bf92397a47becc224798c46fafc944454390335` |
| Full Store suite log | `c37df43568439ced3cd81f76ebcb637fd68679c15aff96c14e2b57438937188d` |
| Focused net8 log | `14cc1053fbce1899feb73a56e742179f1c320044c26654d6420d56464adf577d` |
| Focused net9 log | `8183849306be99dbf4e2fe52c00e610dce197c48bc99958b7148380a0cb41df8` |
| Focused net10 log | `18bc2415ef53680ed7edf4810daf56a222f44a7cc1820c65c0c8dd7581c0bd77` |
| Matching 766-input manifest for final gates | `e337f754636547c858fd99190180c18b2613ecfd2b3fcc6216ffca69dd27e423` |
| Native no-replace causal-control log | `6d0fd0b921d022a0da10769e8f354369554a2dc42d937f46c550513f47b246f6` |
| Canonical basename causal-control log | `ac7ab52abb3d98a2a1f5b1fce53e00ef8b113d4fc4876ac8777cacd350de9fdb` |

Logs, TRX, original mutation backups, source manifests, native integration proposals and independent review reports are retained under the owned `prune-admission-authorization-audit/native-filesystem-gates/` artifact directory. The digest commit's separate hosted qualification is recorded in [digest evidence](canonical-digests.md#hosted-qualification).
