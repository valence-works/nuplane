# Protection-state serialization evidence

T030 preserves the seven-parameter constructor/deconstruction shape of `StoreStateRecord` and adds nullable non-positional `ProtectionRecord` metadata. Legacy absent/null protection remains absence, not KnownEmpty. `StoreRegistry.GetStateAsync` copies this immutable metadata; record `with` mutations retain it.

T031's core codec increment uses a seven-field legacy body DTO plus the optional `protection` property. Older derived `*Normalized` aliases remain readable but are not emitted. The optional descriptor/graph maps retain existing null-to-empty normalization. The default serializer opts into `IPackageProtectionStateSerializer`; a public `PackageProtectionRecordJsonConverter` supports external JSON serializers without making internal record constructors public.

## Validation and authority boundary

The converter preserves independent active and recoverable LKG closures, exact physical identities, install graphs, recovery selections and explicit retirement evidence. It rejects unsupported schema/enum tags, missing required values, malformed digest text, unknown properties, duplicate properties within protection, and structurally inconsistent nested records through their existing constructors. The default state DTO additionally rejects duplicate outer `protection` fields, including null-first payloads. External serializers must validate their own containing envelope; the value converter cannot do that on their behalf.

This is structural serialization, not digest computation or verified publication. Digest-shaped fixtures intentionally contain placeholder hashes. No serializer declaration, Known tag, or successful round trip grants admission/deletion authority. T031 remains **partial** until coordinated publication independently reopens the actual saved state and refuses metadata loss or digest mismatch. Ordinary legacy registry mutations currently preserve metadata without recomputing its body digest; enrolled publication must route those writes through the upcoming coordination protocol.

## Verification

Root reviewed the production DTO/converter, existing normalization and atomic-write paths, registry copy, and all new tests. Review corrected canonical wire naming and duplicate outer protection ambiguity; cleanup retained shared required-array helpers without empty constructor boilerplate. A final independent source review found no remaining blocker; root verified all ten reviewed file hashes. Review report SHA-256: `3b0b8405c59f0c2d9f6fe067ea5c3bfabaaa94238af303ef55f5c2ebc986a922`.

Final all-TFM core build passed .NET 8/9/10 with zero warnings/errors. The full local Store suite passed **226**, failed **0**, with **14** explicit Windows/casefold skips; all **29 new serialization cases** executed. Tests cover legacy compatibility, independent active/LKG graphs, Unknown versus KnownEmpty, external conversion, constructor/deconstruction, registry copies, malformed and ambiguous payloads, and failure-atomic writes.

Removing only the outer duplicate-field rejection caused exactly **two** missing-exception failures (null-first and value-first), with zero skips. Root restored the original DTO byte-for-byte before the final passing build and suite.

| Evidence | SHA-256 |
|---|---|
| Final core all-TFM build log | `630955dfe3ad028db137a6fddf2b6557e4d2cb100ec7b0f13af1ee6aa3fd530d` |
| Final Store suite log | `19e96bd4ac00350982e513f5a60fde7e2ff32ba393c7e0bbb9a11dc1435eef4c` |
| Matching source-input manifest | `ec4b067f19f73134501b08e29b84bb2eeb3c3da7ce7722e25a6dd6ce7dc6d598` |
| Duplicate-guard causal-control log | `e99cb34d1351734c24d0e55d37f32495e8cec554c8d35b422e8f73334a00c0f7` |
| Restored DTO | `a9713f8b2ca58597112ab5a179988ed902531518cb505e25c98e4854d79aea6d` |

Logs, source manifests, TRX, mutation backup/control and independent reviews are retained under the owned `prune-admission-authorization-audit/native-filesystem-gates/` artifact directory. Hosted qualification of this serialization increment remains pending; the previous record commit is qualified separately in [record evidence](protection-records.md).
