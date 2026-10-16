# Internal guarded standalone state writer

This increment implements an internal writer for an already-resolved held parent and exact state slot. It takes the supplemental slot guard, proves permanent group-marker absence before reading prior payloads, and retains the guard through native publication and outcome verification. The retained-path callback validates ancestry and parent evidence without pinning the replaceable final file identity or acquiring root/member locks.

The writer rejects incoming/existing v2 bundles. It streams ordinary payloads without the ledger's 4 MiB quota, preserves original prior bytes in a verified backup, and reopens staged and committed payloads through the selected codec. A final prior-byte hash check follows awaited callbacks and precedes synchronous native verification/publication. Cleanup requires the captured prior identity and bytes or positive initial absence; changed evidence is preserved and refused.

A publisher exception after replacement is classified from actual destination identity and bytes. Verified commits remain observable when backup removal or guard release reports an error. Unknown destination/path/marker evidence refuses with recovery evidence retained. This establishes no power-loss durability guarantee.

## Local verification of `0a1a19636960f08f81bd640e78293d46a9cda267`

Root ran the gates serially on macOS against the same frozen executable inputs:

| Gate | Result |
|---|---|
| Focused native writer suite | 17 passed, zero failed/skipped; unchanged strict exact-case verifier passed |
| Full Store Release/net10 suite | 849 passed, zero failed, 44 explicit platform skips; 893 total |
| New cases in the unmodified full-suite TRX | All 17 appeared exactly once and passed |
| Core Release build | net8/net9/net10, zero warnings/errors |

Omitting only the final prior-byte comparison made the successful-codec/same-inode mutation regression fail because publication incorrectly succeeded. Root restored the exact production bytes before the final full Store/Core gates. An earlier test-adapter compilation failure executed no tests; a subsequent focused run had 16 passes and one incorrect callback-count assertion, corrected to require earlier marker refusal. Those attempts remain preserved. The unchanged Store suite retains its existing `RootMembershipRecordTests.cs:79` xUnit2031 warning.

Frozen artifact hashes:

- Executable-input manifest (1,025 files): `6e0a618c8c693ef39c6cb59a862a8d86fa73db288152755cde76ae63befd8d41`.
- Local qualification: `d5d16fd8cd1cff0f24fa9ab13e50205b21414b3bc661f7dd6293962258109de3`.
- Full Store TRX: `c2aeef9f5a4d6e2c1c416abace253a54a8ed7baeb7f7f333a829a3cfb902dbda`.
- Core log: `0af6c16acb556220d873196871d4e43e3c5ec78489b8813fca0055436b6326f3`.
- Independent final review: `b729ac78d39b4a89fb5d113d3b49c839a772e6e1e290eea890c075f01229f5f7`.

Root and independent review found no remaining actionable issue in the local helper review. Actual hosted validation subsequently found the Windows defect below; local review and green results on other platforms do not override it.

## Hosted qualification of `0a1a19636960f08f81bd640e78293d46a9cda267`

[Validate 38072829645](https://github.com/valence-works/nuplane/actions/runs/38072829645) completed with five successful jobs and one failed Windows job. The full build/test job passed six assemblies: 2,289 passed, zero failed, 48 explicit platform skips (2,337 total). macOS, Linux x64 and Linux ARM64 each passed all 17 required writer cases and the unchanged strict exact-case verifier.

Windows executed 16 passed, one failed and zero skipped. `WriteAsync_CreatesAndReplacesStateWhileOpenDeleteSharingReaderKeepsOldRecord` failed with `NTSTATUS=0xC0000022` during native replacement while its prior delete-sharing reader remained open. The strict verifier did not run after the failed test command. Windows compatibility is therefore unqualified for that candidate.

Root retained all four native job logs and the completed full build/test log. The terminal qualification artifact has SHA-256 `e2d4b1de5f2f61b3a59c3d96c279a09e7f8d2d1e504ee5acc7af6a884eb0b4f5`; the full build/test log has SHA-256 `5409d09f9f23f6e32148ef97b750a0e0498a7f254583c8e149bbde14a28039db`. Hosted TRX artifacts were not uploaded, so these results come from actual job output and verifier output rather than a downloaded TRX.

## Windows replacement correction

Replacement now selects `FileRenameInformationEx` (class 65) with `REPLACE_IF_EXISTS | POSIX_SEMANTICS` on Windows 10 version 1709/build 16299 or later. The qualified x64 held-parent/name layout is unchanged. Earlier Windows retains the existing class-10 operation, and every no-replace operation retains class 10 and its collision behavior. This preserves the earlier operation without claiming open-reader compatibility on those older systems. No bypass-access-check or ignore-readonly flags are used. Microsoft documents the [replacement semantics](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fscc/4217551b-d2c0-42cb-9dc1-69a716cf6d0c) and [class availability](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/ne-wdm-_file_information_class).

The Ex path retains a DELETE-access handle to the exact expected prior destination through synchronous rename. Its share-mode check refuses an existing reader which does not share delete and prevents a new incompatible open during that transition. Source and destination publication handles close before ordinary canonical verification. Existing expected identities, held-parent/name checks and no-replace behavior remain in force.

Two direct Windows regressions require old-reader/prior-byte preservation with new-path replacement and refusal with both files preserved under a non-delete-sharing reader. They explicitly require modern Windows; the actual Windows workflow requires each case exactly once and Passed, so a skip cannot qualify that lane. The original 17 writer cases, manifest and strict verifier remain byte-identical to the failed candidate. Compilation, focused local checks and actual new-head Windows execution must qualify this correction separately from the historical results above.

### Qualification of `3ade69babbc0e077c18de9744071ccfa5383a635`

[Validate 38075160339](https://github.com/valence-works/nuplane/actions/runs/38075160339) completed successfully in all six jobs. Root inspected all four completed native logs: each lane passed all 17 original writer cases with zero failures/skips and the unchanged exact-case verifier. Windows also passed all 21 direct native adapter/publication/parser cases with zero failures/skips; its successful PowerShell gate requires each new reader regression exactly once and Passed. The previously failing open-reader writer case remains unchanged and now passes on actual Windows.

The full solution job passed six assemblies: 2,289 passed, zero failed, 50 explicit platform skips (2,339 total). Root verified all six assembly summaries and all 50 unique skipped identities. Two additional Windows-only tests account for the increase from 48 skips in the preceding Linux full job. Local macOS compilation/focused checks passed all 17 writer cases, with ten explicit Windows-only skips (27 total); every required writer identity appears exactly once and Passed in the unmodified mixed TRX. The strict CLI verifier was not run on that mixed local TRX, and no hosted TRX was uploaded or downloaded.

Root and independent source reviews found no actionable bounded issue. Frozen source/test/workflow hashes remained identical after integration and local checks. The earlier Windows failure is preserved as causal evidence; it is not relabeled as passing. Qualification SHA-256 is `4790e030557cd3a178233befc0285611cb466fb02dc382f61ff026ae1ee6cc23`; independent review is `cc174fcdfd8bbb51009a9c89c465a5be170661d8b6e62f7c1fcb5dc4faf57009`; local mixed TRX is `7a1e0f1839d01a31acdb20ae861a5c4b241656bb6e335deca891228346b6fe92`; full hosted log is `4bf9577e4b2839a2170afdd22b19375a34b9ea3d38e68b56acb293cb7989b74d`. This accepts only the bounded Windows correction and four-platform internal writer gate, not the remaining public routing, restart, pruning or release obligations.

## Remaining boundary

`StoreStateSerializer.SaveAsync` is not routed through this helper. Default path resolution, missing-parent creation, ordinary platform compatibility, runtime wiring, complete authoritative first-read/restart fencing, producer activation and physical pruning remain open. T034/T116 remain unchecked and the overall checklist remains 38/128. No stable release or final Foundation adoption is proved here.
