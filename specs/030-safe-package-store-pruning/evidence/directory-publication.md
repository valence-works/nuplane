# Prepared control-directory publication

This increment supplies the native same-parent, no-replace move needed by initial enrollment. It does not initialize a membership ledger, acknowledge enrollment, enable runtime admission, or delete package trees. T021–T024 and T029 remain open for their remaining integration and qualification requirements.

## Behavior and review

`IPhysicalStoreDirectoryPublicationFileSystem` separates directory publication from single-control-file replacement/removal. A caller must prepare and verify the staged contents, close staged descendants and maintain the quiescent cutover. The implementation validates the held parent, exact staged directory identity, canonical native spelling, same-volume placement, unchanged name profile and positive destination absence. It then uses native no-replace publication and verifies the reopened destination identity and staged-name absence. Observations confer no authority. Any exception may follow a completed move; preserve both entries and resolve the actual outcome rather than retrying with replacement or deleting residue.

Unix uses the existing Linux `renameat2(RENAME_NOREPLACE)` and Darwin `renameatx_np(RENAME_EXCL)` paths. Native canonical directory observation shares enumeration with file observation while retaining regular-file/single-link requirements for files and directory-kind requirements for directories. Both reject device mismatch. Windows opens the source relative to the held parent with DELETE access, no-follow behavior and compatible sharing, then uses the existing x64 handle-rooted rename ABI with replacement disabled. Full native paths are never reopened as authority.

Root integrated separately reviewed Unix and Windows implementations, removed duplicated test openers, corrected common-test refusal expectations to the existing RootMismatch/ExpiredScope contract, and added the missing Unix early same-device check. A second independent source pass found no remaining blocker. There is no dedicated mount-point/cross-volume regression in this increment; the source check is present, and that coverage remains a follow-up. NTFS descendant-sharing behavior still requires the hosted Windows result.

## Final local qualification

All four final gates used the same 788-file source/build/workflow manifest, SHA-256 `cb35ccd6af6f52411f53ca699d90130594af4e534a673f988a8af49d2e65eab0`. Inputs were unchanged during each gate and rechecked before committing. Local execution was macOS ARM64/APFS, not Linux or Windows qualification.

| Gate | Result | Log SHA-256 |
|---|---|---|
| All production target frameworks | .NET 8/9/10, zero warnings/errors | `7d3d99d343db452234440c0ec88af04702cf0b96039d4c7739de03c17ec668f0` |
| Full Store suite | 333 passed, zero failed, 26 explicit platform/casefold skips | `fe2ad54ad9b14de350110907de92f1ca6f1cd0d68e10f9717ef8b88e6eb67b6a` |
| Focused directory publication | 9 passed, zero failed/skipped | `593cd6a33fa5e9f64db46674b193714ba772c581d74e3fb0a4748fc57a211ea9` |
| Actual publisher/recovery processes | 33 passed, zero failed/skipped | `ea8085f29843ad7c3dce4892e73368a0dfdf108ad880e91bca6eab3c6660ff3a` |

Root also ran the exact embedded workflow validators against the focused directory and process TRXs. Tests cover populated-tree identities/contents, occupied files/directories/links, wrong identity/kind, canonical spelling and aliases, Unicode normalization, foreign/closed parents, invalid components, a retained losing stage and native collision without managed preinspection. The latter is distinct from concurrent enrollment proof, which remains unimplemented. The process cases regress the existing state publisher/recovery; they are not directory-enrollment crash tests.

Two deliberate controls each produced exactly one intended failed test, with no passes/skips: replacing the native no-replace move with replacement lost collision refusal; falsifying the observed canonical basename lost alias refusal. Sources were restored byte-for-byte before the final gates. Mutation log SHA-256 values are `9db59786af08b239d8f0bc168ab72d8a16820f214c6fd341b8b39b9168359575` and `4a7c2cc1133a93b660d1b851a373906a94ff26f97784f00d05742e557b012a7f`. The first focused run's wrong test exception expectation is retained as a failed attempt, not counted as passing evidence.

Logs, TRXs, manifests, original mutation backups and hash-pinned independent reports are retained under the owned delivery artifact directory `prune-admission-authorization-audit/native-filesystem-gates/`, with the shared/Unix report in its parent. The workflow demands nine directory-publication cases without skips on each of four platforms and three native alias cases on each owned Linux ext4 casefold volume. Hosted results are pending; local skips grant no platform acceptance. There is no power-loss durability claim or automatic orphan cleanup.
