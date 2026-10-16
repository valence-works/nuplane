# Retained admission ledger replacement and prospective configured roots

Date: 2026-10-09. This increment corrects the exact-head Windows failure recorded in [coordinated publication evidence](coordinated-store-and-observer.md#hosted-qualification-failure). It also supplies the first-run configured-root prerequisite for coordinated runtime admission. Full runtime/driver, graph-lifetime, manual pruning, release and Foundation acceptance remain required; no recursive deletion is implemented.

## Behavior and boundaries

The resolver retains root/control directories, native ledger identity and structural candidate digest, but does not retain a ledger file handle across admitted operations. Initial observation and replay pass the expected native ledger identity into the registry's existing canonical/no-follow `ReadFile`. That same ephemeral read supplies the returned identity; regular single-link checks and named-entry identity checks occur before and after reading. Ordinary Windows file sharing flags are unchanged. The root and all member locks remain held by the operation owner during coordinated publication.

The identical-byte replacement regression now requires the native ledger replacement to succeed while path-resolution evidence remains alive, and then requires replay to refuse the changed file identity. Configured-root and multi-path owner cases share one real two-member read/failure/source/active publication sequence and validate the admitted install path after each ledger-changing write. This exercises refresh of the owner's ledger observation as well as registry reads.

Configured-root admission may return `Unenrolled` for a positively absent ordinary suffix, with null root identity and null owner. It retains the nearest existing native parent, its lookup profile and the first absent parent/name edge, then revalidates the ancestry, aliases, reserved-control absence and missing edge both during resolution replay and immediately before returning. It neither creates directories nor reads member/package payloads nor acquires locks. Missing package/archive/member targets remain strict. Missing suffixes after observed authority, unresolved alias expansions and remaining dot/dotdot components refuse.

The reserved `.nuplane-store` name cannot be used as a prospective root component. ASCII case equivalence is used only after ASCII qualification against a supported observed native profile. A non-ASCII first component is supported only under a strictly case-sensitive, non-normalizing profile; otherwise equivalence is unknown and admission returns `UnsupportedFilesystem`. Later missing components have no observable native parent profile, so non-ASCII components refuse. Unknown profiles also refuse. This does not implement or claim full native Unicode folding. A prospective trailing separator is parsed as a terminal dot component and refuses under the same policy; existing-root traversal remains unchanged.

## Review and local verification

Root review corrected the reserved-name overacceptance and managed-Unicode-fold assumption before integration. Independent review of all eight integrated source/test paths found no remaining concrete blocker. The ephemeral-ledger implementation was independently investigated from the actual Windows failure and reviewed again after merging with the prospective-root change. Root owns final integration and gates.

Focused root verification compiled the combined source and ran the authority, coordinated registry and prospective-root suites: **33 passed, zero failed, two ext4-only skips**. Source input bytes were identical before/after the gate; manifest SHA-256 `a4382cd64e09e76515d5223a10d5ba895793b573790677a8d4f6d5c72bda3a9b`. Log SHA-256 `ec275dfe9ac42134f132445d13feebd5a76448c69b2e04cefd521a7e990dc6b3`; TRX SHA-256 `dea65bf2ad246f6f5ee8571eadeb22a21ca458f42cc883554a6bf9c5a5cb0cdc`. The existing xUnit2031 analyzer warning in unchanged `RootMembershipRecordTests.cs` remains; it is not reported as a clean-warning test build.

Final root verification on the same 828-input manifest passed:

| Gate | Actual result | Log SHA-256 | TRX SHA-256 |
| --- | --- | --- | --- |
| Full Store suite | 466 passed, zero failed, 31 explicit platform skips | `7c6edb89423971e91682fb4ab7a216f30a12f1556d79143957b0fa23be19b6ff` | `2fb11235617e9c1ef1141a2cf86b0e0eb0362f2f3b553c1f8d10ce1339f87301` |
| Native boundary | 64 passed, zero failed, three ext4-only skips; 67 results | `41e2bbd2f4029929e56729fbd77dc7942f9497769e521318cd1855310edab09b` | `4c6dee733755801ee5242c959c85c95049a4b1c30c02df1479907c2605792925` |
| Core builds | net8.0/net9.0/net10.0, zero warnings/errors | `426ed9895094c478e0bd114509a2ad9321dd2cd17fae38e8796e10d885d36f1c` | Not applicable |

A compiled causal control weakened only the reserved-name guard from OR to AND. Both reserved-first/later regressions failed because admission stopped throwing; this demonstrates the tests detect that overacceptance. Log SHA-256 `f557f20f65129cece57150ced028a542ecd49ac294f7eb26dca53bcc30be88f1`; TRX SHA-256 `ba9cc272bfdc53da58d3cc4f55908bc000ea839b2ae1df9f6c279578b06c080f`. Source was restored byte-for-byte before the final Store/native/build gates. No Runtime suite execution is claimed for this source increment.

Exact pushed head `04a200ff40e18e9a8b44ac627ca4de07b435dedd` passed [Validate 37982828544](https://github.com/valence-works/nuplane/actions/runs/37982828544), all six jobs including Windows. Root checked exact-head metadata and complete logs: full Ubuntu solution **1,796 passed, zero failed, 31 explicit platform skips** (Runtime 834; Store 466). The native boundary produced exactly 67 results on each runner: Unix **64 passed/three ext4-only skips**; Windows **63 passed/four explicit platform skips**. All twelve framework/platform serialization lanes passed 97 cases each; both owned Linux x64/ARM64 ext4 lanes passed all eight without skips. Complete hosted log SHA-256 `db4e20b4906b928c4f92442eb94d6e8af2cbe2c2f8e16e20b7155d8c1d999b74`. This qualifies ledger replacement and prospective-root admission for this bounded increment; it does not qualify later source changes or full runtime/pruning behavior.

## Hosted manifest

The native boundary now requires exactly 67 results: 57 unique required facts, six unique malformed-path theory rows and four explicit platform-dependent outcomes. It includes every prospective-root case and the retained multi-path coordinated-publication case. Expected local Unix outcome is 64 passes/three ext4-only skips; Windows expectation is 63 passes/four explicit skips. Expectations are not execution evidence. The owned Linux x64 and ARM64 ext4 lanes now require eight unique non-skipped cases, including reserved-name Unicode casefold refusal. All twelve embedded Python blocks compile. The extracted boundary validator accepted the actual final 67-result macOS TRX. Six cardinality-preserving negative controls were all refused: missing first-edge case, duplicated multi-path case, skipped required case, falsely passing conditional ext4 case, duplicated theory case and missing coordinated case. Runner matrix, triggers, permissions and concurrency policy are unchanged.

## Integrated source pins

| Path | SHA-256 |
| --- | --- |
| `src/Nuplane/Store/Coordination/PackageStoreAdmission.cs` | `6b0809361550c3b88d9e6e2a3353be3f97a7868b1854938e2a89a68f08d8fc2b` |
| `src/Nuplane/Store/Coordination/PackageStoreAuthorityResolver.cs` | `1dbe79ad69622779b0afbcdd594f93ff3a021668e1855f0c57a44441bb0a296f` |
| `src/Nuplane/Store/Coordination/PhysicalStorePathTarget.cs` | `ec53d924f65f9f751c0706b85be99fe079208335493d22a12b3015ed4b9b6e5e` |
| `src/Nuplane/Store/Coordination/ResolvedPackageStorePath.cs` | `f106e2540fa9becfc4adf469455e9ef39b99ee819511071e189e4d9ae11e96f4` |
| `src/Nuplane/Store/Coordination/RootMembershipRegistry.cs` | `ac9fbfd37dbfb13ee370dfd44ed03714cff7868de6b00436e829a7b59c09d180` |
| `test/Nuplane.Store.Tests/Coordination/CoordinatedStoreRegistryTests.cs` | `bfca79739926882676f9c1a142e04f5e543d7bbce6d282b594fa963472c4d362` |
| `test/Nuplane.Store.Tests/Coordination/PackageStoreAdmissionProspectiveRootTests.cs` | `20963f1b119f3c58b4cafee50027aa5c884339b95f3b15664494dc5280fce862` |
| `test/Nuplane.Store.Tests/Coordination/PackageStoreAuthorityResolverTests.cs` | `82fd83617bd343716707db1f5f48b31a453fd1b5d15389e1329a7f362556fbbd` |
| `.github/workflows/validate.yml` | `581e9d37eda6cebe387971e4ede1ec78a51965443a7a3a4195bed75a59fb7263` |
