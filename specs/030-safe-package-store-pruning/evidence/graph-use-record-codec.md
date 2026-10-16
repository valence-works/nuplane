# Descriptive graph-use record codec and Windows cursor correction

## Scope and authority boundary

The internal schema-1 `GraphUseRecord` copies the complete active-only graph,
including every root/request/node/edge, pending/committed snapshot state, root and
positive enrollment epoch, nonempty use ID, exact sentinel identity, OS-exclusive
sentinel mechanism, diagnostic PID and canonical digest. Native identities must
agree with their own roots' providers and volumes. Sentinel and root identities
must differ. Additional graph roots are preserved; their presence is not liveness
or admission proof. PID is diagnostic only.

The strict codec rejects unknown, missing, duplicate and case-mismatched fields,
unsupported schema/enums, inconsistent identities, mismatched digests, truncated
or over-deep JSON and payloads larger than 4 MiB. Its domain-separated digest binds
every record field except the stored digest, reusing the existing full-graph
canonical encoding without changing earlier digest formats. The independent
revision-1 golden vector is
`66be5e670735a4f33b4e29fbda2011fa2d2cfa13503694cfa6c95518b5e38b86`.

This result is descriptive data, never a `PackageGraphUseSnapshot` capability.
Native graph binding, sentinel acquisition, immutable record publication/reopen,
nonblocking live/stale inspection and actual Loading integration remain required
under T036 and the production-driver tasks. No deletion primitive is introduced.

## Actual local execution

All three actual frameworks, `net8.0`, `net9.0` and `net10.0`, passed **123 cases
each, zero failures/skips**: 26 graph-use cases and 97 preserved serialization/
digest cases. Their common unchanged 859-input manifest is
`fd81c6a68c6ed11dc7c167f6b832f87bd73366e5fb965272778fbce4f4133a0c`.
The initial focused attempt preserved one test failure: truncated JSON throws a
valid derived `JsonException`; the assertion was corrected to accept subtypes.
The corrected focused run passed 54 cases, including the independent vector.

After adding the Windows correction and final CI accounting, the full restored
Store suite passed **557, zero failures, 38 explicit platform skips**. Loading/core
built in Release for all three production frameworks with zero warnings/errors.
Both final gates retained identical source inputs; common manifest SHA-256 is
`9b6db7425203ae402b25802f605adb32a88ce9d36c6e7f132e1b6c2c74d6128e`.
Store log SHA-256:
`3b3f6364855db920e3a4710edd29960e79d255cfe5a8aec0c7b3ce7ffaea0bc7`.
Build log SHA-256:
`0bf6ea46595dba8bb963813255133511ff40d40ad6a94c808d9da96445203479`.
Store test compilation retains the existing xUnit2031 warning; the separate
production build is the warning-free result. Local Windows skips grant no native
Windows runtime credit.

## Causal controls and review

Each guard-removal control compiled and failed at its intended assertion. Source
was restored byte-for-byte before the final Store/build gates.

| Removed guard | Intended failure | TRX SHA-256 |
| --- | --- | --- |
| Stored-versus-computed digest check | Tampered PID no longer refused | `f35d5ec36a95af8209a0107ef00d4e42e120439f83afd36a8934db7d35d9ba0d` |
| Recursive duplicate-property check | Duplicate JSON no longer refused | `3618f97e6b31cb6589c4703eb0980d765234b8bdf97ec72af58e0fb91e847893` |
| Sentinel identity digest tag | Independent canonical vector differs | `b6bf6dd630f9d53f6bd11f75359daf2d680839dfa81075c196e67e4473870a59` |

Independent/root review corrected the entity-table omission of snapshot state and
stored digest. Root also tightened legacy-suite counts and duplicate-result
accounting. The final source review has no material finding. All 19 embedded
Python blocks compile, the framework-loop shell syntax validates, and its codec
validator accepts all three actual 123-case TRXs. Twenty-four malformed-result
controls reject missing, duplicate, skipped, failed, unexpected, empty and
substituted legacy results. Workflow SHA-256 is
`59aecddb6204e0d72903abb4e9d9e4a93761eef26318f1d54cf4ebaf42cbd114`.

The [Windows cursor correction](native-root-access-and-enumeration.md) preserves
the seven-case native gate after the original `2be238f` run failed. Its final
adapter/native source SHA-256 pins are respectively
`2ce271be10696ef00d6e85f7d288cb5172c3f689215138f24ff3bb612fed3891`
and `850be17ae21b7cf417be5b051f41f0933e02f5f84106eba082b2329112a9fa2b`.
Exact head `22cdc9177673e904174c1a97dcf5db857b895bfa` passed all six jobs in
[Validate 37998468984](https://github.com/valence-works/nuplane/actions/runs/37998468984),
including Windows. The full Ubuntu solution passed 1,909 cases with zero failures
and 38 explicit platform skips. All four native lanes passed their required
root-access, enumeration, metadata and lifetime cases without skips; all twelve
platform/framework codec executions passed 123 cases each. That qualification
applies to this codec/cursor head, not subsequent native publication changes.
Checklist acceptance stays
**32/128**, with T036 open. Stable releases and Foundation host/e2e adoption remain
program obligations after complete pruning and before-read lifetime acceptance.
