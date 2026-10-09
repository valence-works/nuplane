# Bounded native graph-use inspection

## Implemented boundary

`PackageGraphUseRecordStore.InspectAsync` consumes an already-held physical root
and the caller's current Complete membership. The production caller must retain
the root and all member locks and verify every enrolled state. Inspection grants
no package-read capability, cleanup authority or deletion authority.

Held-parent enumeration is bounded to 4,096 control-directory entries. Only
exact lowercase `use-<GuidN>.json` and `.sentinel` pairs are accepted. Unknown,
staged, orphaned, duplicate or aliased use names refuse the entire inventory.
The control directory, record and sentinel identities, canonical names, file
kinds, link counts, volume and root epoch must replay. Records must satisfy the
strict codec and exact byte/digest comparison. Foreign-root graphs are refused.
Every graph install is reopened relative to the held root and must retain the
recorded directory and completion-marker identity; package payloads are not read.

Sentinel ownership is probed without waiting. Busy valid sentinels classify the
full graph as Live. An acquired sentinel must replay before classification as
Stale, and inspection releases that probe before returning. PID is diagnostic
only. Classification never removes any artifact. Missing or uncertain evidence
refuses the inventory; there is no silent orphan or stage repair.

The cumulative serialized inventory budget defaults to 64 MiB, in addition to
the per-record 4 MiB limit. Reservation occurs from native observed record length
before opening or parsing its payload. Reads cannot exceed that observed length,
and the returned length must match. A smaller internal budget supports the
bounded native regression; it cannot exceed the production limit. Cancellation
and errors unwind observations and probe locks; uncertain cleanup resources and
primary errors are preserved.

## Review and executable proof

Root and independent review closed the cumulative-budget ordering finding.
The budget regression first measures actual reads for one real record, then
publishes a second and proves refusal without opening that second payload or
probing its installs. This replaced an incorrect test-only assumption that each
completion marker opens once; native identity replay legitimately opens it more
than once. The failed initial run remains preserved. Successful Stale results
are followed by actual native lock reacquisition for both sentinels.

The 15 new inspection cases cover Live/Stale and Pending/Committed records,
full graphs with a shared dependency, empty inventories, cancellation after lock
acquisition, aggregate budget, missing pairs, staged/aliased names, malformed and
digest-mismatched records, wrong sentinel/epoch/root, missing completion evidence,
hard links and bounded enumeration overflow. The final focused gate passed
**34 native cases, zero failures/skips**, including the existing 19 publication,
binding and acquisition cases. Its synthetic Complete membership is structural
fixture input; it does not qualify production membership admission.

A compiled causal control removed only the persisted/native sentinel-identity
comparison. The digest-valid wrong-sentinel regression failed because no
exception was thrown. Source was restored byte-for-byte; control TRX SHA-256 is
`f911f084b88b08165cfed94484b8d67a433a6afa9efa092920ef9c956e115863`.
The independently reviewed restored inspection source SHA-256 is
`537cb99f1ebf218830e4199bb9f27fdd2c682f8ad2a7315e16012b0655c233aa`.

Final Release Loading/core builds passed .NET 8/9/10 with zero warnings/errors;
the complete Store suite passed **599/0/38 explicit platform skips**. Both gates
used unchanged 867-input manifest SHA-256
`1af78065a642a286a84c490b1fee153969e0a25d545cd3fbcbd7a8b3cb4d29c6`;
final Store TRX SHA-256 is
`443583a843a2a6540081a45268049c5c31f399d00921b247aad5098497b4e791`.
The existing test-only xUnit2031 warning remains. Workflow verification compiles
all 20 embedded Python blocks, parses affected shell steps, accepts actual
34-native/17-lifetime results and rejects all 14 malformed-result controls.
Hosted qualification of this increment remains pending.

## Remaining delivery

T036 and T047 remain open, accepted tasks 32/128. The production public provider,
real Loading handoff and actual overlapping-generation first-read proof remain
required before recursive deletion begins. Stale cleanup/reaping, inventory and
retention, confirmed fresh planning, native quarantine/deletion and crash proof
remain separate work. Stable upstream packages and Foundation Host/Workbench
adoption with end-to-end verification remain part of the full program.
