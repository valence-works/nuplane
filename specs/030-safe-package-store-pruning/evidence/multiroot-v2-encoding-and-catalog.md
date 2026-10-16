# Multiroot v2 encoding and trusted catalog prerequisites

This increment supplies the descriptive schema-2 protection bundle and immutable configured-root catalog required by the [multiroot protocol](../contracts/multiroot-protection.md). It enables no group producer or recursive deletion. Existing membership ledgers and their ordinary publication/recovery protocol remain schema 1.

## Implemented behavior

The state payload may carry one optional `protectionBundle`, mutually exclusive with the legacy `protection` field. Immutable bundle/root-row values separate the common state generation from each root-local epoch and revision. Rows retain full Active/LKG closures, root-local retirement data and legacy-unknown recovery evidence. Domain-separated participant-set, row and bundle digests bind these values. The existing state-body digest excludes protection metadata. Strict conversion rejects mixed, null-present, duplicate, unknown, missing and tampered representations. Copy paths retain the bundle.

`StoreStateRecord` keeps its seven positional constructor/deconstruction values. The optional property is omitted when null, including external default System.Text.Json serialization. An actual literal whole-file JSON test preserves legacy bytes; existing canonical v1 tests remain unchanged. Legacy direct/coordinated registry writers, declared-member binding and root-local publication/recovery refuse a v2 state before stripping or replacing its protection.

`NuplaneBuilder.AddPackageStoreRoot(label, locator)` records unique case-insensitive labels with a reserved late-options `default`. Additional roots freeze against the callback's final BasePath or current directory. Windows rooted-but-relative locators use deterministic `Path.GetFullPath(locator, basePath)` semantics; same-drive, volume-relative and other-drive cases have explicit assertions. Catalog resolution performs no native probing, enrollment or filesystem mutation. Repeated builder calls retain prior immutable entries, while captured-builder mutations cannot alter a provider's catalog. Real AddNuplane and host-free RestoreComposition checks resolve the catalog alongside existing registry/admission services without a dependency cycle. See the [usage guide](../../../docs/wiki/Usage-Guide.md#named-package-store-root-locators).

## Review and integration

Frozen encoding worker `5e5f3883f0164ae01f02115ac5ad6f7b23413f38` is integrated as `8c7eab7`; root verified all fourteen artifact checksums and eighteen source pins. Independent implementation review SHA-256 `498090f81ffe4aeecb891834ab25aabb5b06b30a48367b804854c237776f5d27` found no production blocker. Root filled the legacy whole-file byte and actual native declared-binding refusal gaps, then isolated leaf-field tampering by modifying every descriptive graph copy consistently. Final independent test/CI review `477425195ac228b8fedd5e0478987e61ba2d785500376138851427825cca5149` is clean for that bounded surface.

Frozen catalog worker `523a50253bbbd33713babd604b5a45de794c2257` is integrated as `290a383`; root verified all six selected source/contract pins. The worker ran no tests. Independent/root review isolated the CWD-changing test from parallel collections and corrected `IsPathRooted` classification to `IsPathFullyQualified` before deterministic base resolution. The first actual focused run passed four and failed one due to macOS `/var` versus `/private/var` spelling; the test now observes the actual frozen current directory. Subsequent focused runs passed all five. Final independent catalog review `1090126245cc997544fc776b12455d3230b93235f8f5367d7a4fa06c7d8e4f8e` found no remaining actionable finding. Root's review loop stopped clean after three catalog iterations and two encoding iterations.

Earlier failed worker attempts remain preserved, including the actual legacy null-property serialization regression that led to explicit omission. No failed or skipped invocation counts as a passing gate.

## Actual local verification

Root ran nine serialized Release gates against one unchanged 994-input manifest, SHA-256 `07e67349381a0d0701efbcb50dbd1d77a5727e3509a1d09b606bc9edf623e3f6`:

| Gate | Actual result |
|---|---|
| Core build net8/net9/net10 | Zero warnings/errors |
| V2 encoding/refusal, net8 | 29 passed, no failures/skips |
| V2 encoding/refusal, net9 | 29 passed, no failures/skips |
| Full Store, net10 | 776 passed, zero failed, 42 platform skips |
| Full Runtime, net10 | 905 passed, zero failed, 2 platform skips |
| Full Integration, net10 | 232 passed, no failures/skips |
| Full Loading, net10 | 259 passed, no failures/skips |
| Full Directory, net10 | 21 passed, no failures/skips |
| Full NuGet, net10 | 25 passed, no failures/skips |

Full-suite total: **2218 passed / zero failed / 44 explicit platform skips**. Qualification `251714f4de172b7464cd0d06d52a6d46b88eb7db822922a17320df4bd2d5b849` pins commands, logs, TRXs and every skip identity. These are actual macOS results. The v2 29-case and catalog five-case identities each appear exactly once and passed in their corresponding full-suite TRX.

After those nine gates, root added only a Windows-conditional cross-drive assertion and explicit XML/guide wording. Input comparison proves only the builder XML and catalog test changed within the hashed input set; the usage guide is outside that set. Final focused catalog v3 passed **5/0/0** with unchanged inputs, manifest `f82dc849f7a41954e7d198d152f70dbe717eff4c3351b56109911b5cff436b68`, log `dba9e2b4f3b3fc3ec691b63987abe0e096046c45f127748b8134e0e5f1cb23cd`. Final-delta qualification `ecf5b76554b2091def8f14e4d5edfc9a6c76e410c75e0efb04317bc544c7c431` records that exact relationship; the nine gates are not misrepresented as sharing the final assertion's input hash.

Two compiled omission controls failed the intended actual test, then exact production bytes were restored and the full Store suite passed: omitting the declared-binding v2 guard failed `BindDeclaredMembersAsync_ProtectedOrMalformedPriorStateRemainsDeclared` (log `3f8a4bbe002f04605c46bbf8112c1aa2319ac605c64c79c33a73a353460cef3a`); omitting null-property exclusion failed the exact legacy JSON/external-default test (log `55bca41deaf3e23c4c6ce14cea03218186fd4884316e54358ab2468267356101`). These are behavioral failures, not compilation failures.

CI adds exact manifests requiring 29 v2 cases on net8/net9/net10 and five real catalog composition cases on each native platform. The existing 123-case serialization/digest step is unchanged as a parsed YAML step. Both new manifests accept the actual passed rows and reject empty, missing, duplicate, unexpected, failed and skipped controls: twelve malformed controls rejected and two actual positive subsets accepted. YAML parsing and every bash step's syntax check pass. Malformed controls prove accounting only. Fresh exact-head hosted qualification, including actual Windows assertions, remains pending.

## Remaining authority and acceptance limits

The catalog is configuration data and a bundle is descriptive evidence. Neither establishes full native group ownership, schema-2 ledger acknowledgement, publication or recovery. The legacy path-based save probe is not a race-safe group fence; its probe/replace interval and all first-Intent untouched-participant paths must be covered before enabling a group producer. Native group recovery needs a sibling owner that accepts exact pending/Incomplete participants rather than weakening ordinary Complete-only admission.

The next implementation must preserve unrelated schema-1 members in schema-2 ledgers, immutable Intent versus artifact-bound phases, all-participant monotonic Next decisions before any Complete acknowledgement, and fresh full-group authoritative-use fencing. Offline HostIntegratedLoader remains in scope. Full drivers, historical migration, explicit configured-label enrollment/preview/execute, actual safe deletion and crash recovery, stable Nuplane/CShells releases (including CShells.Nuplane), and Foundation adoption/e2e/resulting-main proof remain required. Checklist remains **38/128**; PR112 is not merge/release ready. Copilot is unavailable and Greptile optional by owner instruction; neither is claimed as approval.
