# Native Windows filesystem foundation

Date: 2026-10-09. Base: `aa46e0712c87df1bd5d9d5d9843a7b6429d17e9a`.

This increment adds the Windows implementation of the existing non-destructive
`IPhysicalStoreFileSystem` boundary. T023 remains partial; it is not acceptance of
root admission, complete enrollment, graph lifetime, state replacement or deletion.
The shared physical-root/multi-state active and recoverable-LKG contract is unchanged.

## Support and mechanism boundary

The initial adapter permits Windows **OS and process x64**, on positively identified
local disk **NTFS** volumes. Other architectures, remote/non-disk devices and other
filesystems refuse. Namespace anchors are strict drive or volume-GUID roots. A drive
root must match the actual volume-GUID root identity, preventing a redirected/SUBST
subdirectory from masquerading as the terminal physical namespace root.

Child opens and exclusive creation use single-component `NtCreateFile` relative to a
held parent with `FILE_OPEN_REPARSE_POINT`. Full 128-bit file ID plus volume serial,
type, length and hard-link count are inspected through held handles. Native error and
unsupported results become typed admission refusals; only positive child absence and
positive lock conflict become null. No payload is read by a metadata probe.

Directory SafeHandles own shared reference-counted ancestry frames containing an
owned parent duplicate, exact component, child/parent identities and parent frame.
Ascent rechecks the edge before returning a duplicate of the already-held parent.
There is no native `..` traversal or remembered-path reopening. Directory handles omit
delete sharing; actual Windows qualification must demonstrate the rename conflict
and retained-parent lifetime. SafeHandle final release owns ancestry cleanup, so
operation leases survive caller wrapper disposal.

Control files require regular single-link identity. Reads are bounded by the caller;
exclusive files permit one initial write and file-only flush. Nonblocking exclusive
byte-range locks retain the native handle lease through unlock, and a failed unlock
poisons future attempts on that wrapper. This does not claim directory-entry or
power-loss durability, nor cross-process acceptance beyond the future process gates.

Bounded reparse decoding supports Microsoft symbolic-link and mount-point payloads,
strict UTF-16, expected identity, valid offsets and consistent relative/absolute target
forms. Targets remain descriptive input to the future component-wise authority
resolver. Configured aliases can be resolved later; final package/member/control
links remain refused. Unknown tags and malformed targets refuse.

## Review and local gates

Root review plus a separate reviewer covered native ABI, handle/frame ownership,
error typing, root/device identity, reparse boundaries, tests and workflow. Review
found and corrected a missing native query buffer-size argument, raw error paths,
relative-target flag handling, an unselected provider size limit, a test handle leak,
and synchronous status checks. The first core build caught a named-argument casing
error; it is preserved as a failed gate. Four bounded root review passes ended with
no remaining static findings. Actual Windows execution is still required.

Owned artifacts are under
`/Users/sipke/.codex-workspaces/artifacts/modular-hosting-2500/prune-admission-authorization-audit/native-filesystem-gates/`.
Every build/test runner pins all 716 source/configuration inputs before and after and
uses the owned NuGet cache and serial machine build-slot wrapper.

| Gate | Result | Log SHA-256 |
|---|---|---|
| `windows-core-build-v1` | Preserved development failure: named argument casing, 3 TFM errors | `fd047066be1a5d538d2eae4158ccd267cae92999bc0c42677321333887089ca3` |
| `windows-core-build-v3` | Final core Release build, net8/net9/net10; zero warnings/errors | `ce3f8c298729c933bd483bd797859cd2a058f22e0f8d2579f92cb7dac539c019` |
| `windows-store-tests-v2` | macOS arm64: 131 passed, 7 explicit Windows skips, zero failures; includes 7 actual Unix native and 3 pure parser cases | `94a55eb96205998dd33f571ecf31c0073dac38f6335f4db08963c0509e0971e7` |
| `windows-utf16-mutation-v1` | One expected failing test after strict decoding disabled | `568b415e302addb57ac9126620478485a1e31aeabb9917590268fe3ecd8d757f` |

The mutation disables `throwOnInvalidBytes` only in the Windows target decoder. The
malformed UTF-16 case then fails because no typed refusal is thrown. The adapter is
restored byte-for-byte to SHA-256
`d191ab559d454dcd163e696afd476b823fadf86db3707ae74f3c2bf105697bbf`.
The final Store run after restoration and the volume-query correction is recorded
above. Final build and Store inputs share manifest SHA-256
`5598559744021b1f114b9ec123989c4875bcfe51f727046f31476cde81f4f234`.

The independent final review records nine verified source/workflow hashes;
`windows-final-review.md` SHA-256 is
`15f3687ac0f842c5436bf0e4b648eaf60c5a89a84fa7e7ecf70aef2fdecc4541`.
Root verified those hashes against the restored candidate. The native helper hash is
`d4dcd280ebaa6070b7964100c0278bdefce1354ed9f346d9f37aa1a81c9f49d8`.
`actionlint .github/workflows/validate.yml` and `git diff --check` pass.

## Hosted qualification still pending

The existing Windows state-persistence job additionally runs the seven native
Windows cases, the unsupported-runtime check and three pure parser cases. The lane
asserts OS x64 and an NTFS temporary root, requires at least **11 passed tests with
zero skips**, and fails on missing TRX counters. Local macOS skips do not count as
Windows acceptance. Existing Darwin native and Linux/package-asset gates remain.

T021/T022/T023 and platform acceptance remain open for the remaining operations and
actual qualification. No runtime service registration, package deletion, recursive
walker, release or Foundation adoption is included in this increment.
