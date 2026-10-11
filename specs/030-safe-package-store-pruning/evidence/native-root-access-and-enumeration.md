# Counted root access and native child enumeration

This bounded T036 prerequisite follows qualified metadata/lifetime head
`13dc3745ecb5406beee62448900f9af30544bfd9`. It does not implement the graph-use
record/provider, Loading attachment, stale-use recovery, or physical deletion.

## Ownership and native authority

`PackageStoreOperationAccess.WithValidatedRootAsync` uses the existing admitted
root and all-member ownership. The counted borrow remains in flight across gate
waits, callback awaits, and complete-member replay. Path-restricted borrows cannot
expand into unrestricted root callbacks. The callback runs under the same
serialization gate as state publication and reading; it must not reenter that
gate or retain the supplied root handle. Complete acknowledged member payloads
are independently checked before work, after success, and after exceptions.
Cancellation after the callback cannot bypass the final uncancelled replay.

The optional internal `IPhysicalStoreDirectoryEnumerationFileSystem` companion
preserves the existing required filesystem interface. Unix and Windows adapters
retain the provider-owned parent handle and enumerate a fresh native cursor
opened relative to that parent. They check directory identity and name profile
before and after, filter only dot entries, validate exact encoding and portable
components, reject duplicates and global entry overflow, and return detached
ordinally sorted names. A failed or uncertain scan returns no partial list.
These names are observations; they grant neither atomic-snapshot nor deletion
authority. No diagnostic full path is reopened by either implementation.

Darwin uses a fixed-signature C `openat`/`fdopendir`/`readdir` bridge with checked
allocation and cleanup; Linux uses a separate held-parent `getdents64` cursor on
the qualified x64/ARM64 ABIs. Windows uses a distinct relative directory open
and bounded `NtQueryDirectoryFile` calls. Its pure record/status tests reject
truncation, malformed UTF-16, duplicate names, and inconsistent completion data.
The first empty-query status handling follows Microsoft's
[directory query contract](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fsa/fa8194e0-53ec-413b-8315-e8fa85396fd8);
record layout and cursor behavior were checked against
[FILE_NAMES_INFORMATION](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_names_information)
and [NtQueryDirectoryFile](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntquerydirectoryfile).

## Local qualification

On the owned macOS/APFS root, the focused gate passed all 24 cases with no skips:
15 root/state ownership cases, seven Unix native enumeration cases, and two
portable Windows parser/status cases. Native tests include simultaneous
independent scans, exact Unicode and link names, entry limits across multiple
native batches, and a renamed Unix parent whose old textual locator is replaced.
Windows-native tests separately exercise more than 64 KiB of directory records
and the provider's delete-sharing exclusion; those require Windows execution.

After all six causal controls below and byte-exact source restoration, the full
Store suite passed **531, failed zero, skipped 38** explicitly platform-inapplicable
cases. Source/test/workflow inputs remained unchanged during that final run.

- Full Store input-manifest SHA-256: `cd6194f1a53b3a9e145b17571aa75d2d1116622f46f06c675da0eb8c647d1f45`.
- Full Store log SHA-256: `d1fdc247d9a8d970ec9d007e3b65d064217041af94c00dae0acfe67e623a1d89`.
- Full Store TRX SHA-256: `fd516f22f75da0d84259c5a9d38092594750d95b434ed18c7632b0f794f0a777`.

`dotnet build src/Nuplane.Loading/Nuplane.Loading.csproj --configuration Release
--no-restore` then built Loading/core and their dependencies for all three
production frameworks (`net8.0`, `net9.0`, `net10.0`) with zero warnings/errors.
Its source-input manifest is identical to the final Store manifest above; build
log SHA-256 is `3c1e0a991b4fd893ec8ff8c077f33431ad9dfe30d3692064d884409ad0c4dde1`.
The Store test compilation separately reports the pre-existing xUnit2031 warning
in `RootMembershipRecordTests`; it is not represented as a warning-free test build.

Each control compiled and ran its intended regression to an assertion failure;
compilation failures and observation timeouts provide no behavioral credit.

| Removed safeguard | Observed intended failure | TRX SHA-256 |
| --- | --- | --- |
| Before-callback complete-member verification | Invalid member still allowed one callback | `2016aa828152ef1ae434cc7aab248278868c72a55e63c6b91a89f9a002cb02da` |
| After-success complete-member verification | Expected typed refusal was absent | `d033d91aff3116c820049df3e86987fc4853fa0a030f07f5f93701c6d012bc7c` |
| After-exception complete-member replay | Original IOException escaped instead of typed refusal | `5d94c4b149c6d339e4c0271dd9ee5eb39f82c836747260ebb0af87718fc8fa79` |
| Exact enumeration bound propagation and returned-count check | Overflowing multi-batch enumeration returned names | `66032c1541a629b58d8fff2b3a2d14dcd4566699636d21ce6d9ae8761eb24703` |
| Portable component validation | Invalid Unix component returned without refusal | `baaf722b7824257026649a8276d93d059378cebbd3846ccad65e9365615a89dd` |
| First-query restriction on Windows empty status | Later NO_SUCH_FILE was accepted as completion | `707b9b167e4a03bf34856bf26ef4c391f5b8afcf04639fb56ecbe3bd042b0819` |

## Review and platform accounting

Root and independent source reviews closed the Windows empty-status handling
finding and corrected an overly broad count edit in two existing nine-case CI
validators. Final review found no remaining source issue in this increment.
Unix tests reuse the existing owned context and teardown rather than duplicate
fixture setup. All fourteen reviewed source/test/workflow SHA-256 pins match.

The final workflow adds exact-name/multiplicity/no-skip gates for 15 root-access
cases on all four supported lanes, seven enumeration cases on Windows, nine on
Unix, and the tenth separate owned-ext4 case on both Linux architectures. All
18 embedded Python blocks compile. The new validators accept actual local TRX
subsets for 15 root-access and nine Unix/portable cases. Thirty negative accounting
controls reject missing, duplicate, skipped, failed, unexpected, and empty results.
Windows and ext4 positive accounting inputs are synthetic locally and provide
no native runtime credit. Both unchanged nine-case validators also accept their
actual earlier Store TRX subsets. Workflow SHA-256 is
`0fc3b0c4319b26fdc168f6497b3fd8acb87ef75b73da973ba8116279de90e995`.

The measurements above describe the original `2be238f` increment. Its hosted
[Validate 37996462506](https://github.com/valence-works/nuplane/actions/runs/37996462506)
finished with five successful jobs and a failed Windows enumeration gate. Four
actual Windows cases failed before enumeration because `NtCreateFile(parent, ".")`
returned `0xC0000033` (invalid object name). Failed-log SHA-256 is
`1b5e65bcf6e2c9ac3b127f3560d648a150d6161797cacf66fd052c655c6fe816`.
The portable parser/status tests passed; they do not qualify native cursor acquisition.

The correction opens a fresh independent cursor with `OpenFileById`, supplying
the retained directory as volume hint and its verified 64-bit NTFS file reference.
Microsoft documents [the volume hint and directory/reparse flags](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-openfilebyid)
and [the file-ID descriptor layout](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_descriptor).
The provider refuses unsupported ID shapes, retains the parent, and rechecks both
parent and cursor identity/kind/name profiles. It performs no path reopen and
preserves all seven Windows gate cases. Independent source review is separate
from required native Windows CI qualification; a prepared correction is not a
passing result. Combined correction/codec gates are recorded in
[graph-use record evidence](graph-use-record-codec.md).

T021–T026 and T036 remain open for complete native authority, publication and
runtime integration. The accepted checklist remains **32/128**; that count is not
a program completion percentage. The real two-composition overlap/before-first-read
proof is still mandatory before recursive deletion.
