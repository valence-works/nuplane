# Metadata refusal result compatibility

T081/T082 are accepted for the additive result contract and its compatibility proof. T080 reader production and all runtime consumption remain open: the existing reader does not yet emit this result, and this increment does not protect package reads.

`NuplanePackageMetadataReadResult` keeps its four positional constructor/deconstruction members. The non-positional, init-only nullable `AdmissionRefusalReason` distinguishes a typed admission refusal from Missing and invalid metadata. `Refused(reason)` rejects unknown enum values, supplies a fixed diagnostic and claims neither metadata existence nor absence. Consumers must check the discriminator before interpreting `MetadataFound == false` as absence. Existing Missing/Invalid/Valid construction remains unchanged.

Nine new compatibility cases compile the original four-argument constructor/deconstruction, preserve every defined refusal reason through record copy and JSON round-trip, verify distinctness from Missing/Invalid and reject unknown enum values. Root and independent source review found no actionable defect; no runtime enforcement is claimed.

Final unchanged-source manifest: `4fed0cc9f7a2120a6223a03b6e813f41f96dcdc337fc23ce2e218413c8cc14ca`.

| Gate | Result | Log SHA-256 |
|---|---|---|
| Compatibility plus existing metadata reader tests | 43 passed, zero failed/skipped | `7d5ad121c514490dce8a20d75e7b08c11228c2580eef8045cadb0b30b5230407` |
| Core net8/net9/net10 build | Zero warnings/errors | `2d0db164e32a15a20fc4f50cc7b3a03868ce762725e3ee1e4773145727a55d5e` |
| Full Runtime suite | 808 passed, zero failed/skipped | `0606d3feaa80c524fafd7281bd9e6f6d9edcc46bff7bb7797870f17ee7b8ef85` |

Reviewed source SHA-256: result `d6ab69ec112bd754e2d291555712ff725197275129fb79e4f8b7ff9b7195b9b5`; tests `8285e783608d59a11eaf0cd56ef3b9dc7f7e6da83d0eed4d3bf6d2e0fe4e440d`. Logs, TRX, source manifests and final gate records are retained under the program's owned `native-filesystem-gates/metadata-refusal-*` artifacts. Hosted validation of the new pushed head remains required; preceding `9e4dc07` qualification covers only its earlier enrollment-completion scope.
