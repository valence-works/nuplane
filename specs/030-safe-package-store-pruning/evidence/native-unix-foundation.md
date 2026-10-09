# Native Unix filesystem foundation increment

Reviewed and locally qualified on 2026-10-09 against base `4cafbc66e7e96ab1e0a78e6d45c49028586357c9` plus the frozen input manifests below. **T021/T022 remain partial**, and the accepted task count remains 19/128. This is not runtime admission, safe pruning, complete platform qualification or release acceptance.

The internal filesystem boundary now provides provider-owned held handles, no-follow child metadata/opening, bounded link/control reads, exclusive creation and one-shot initial writes, and nonblocking native file locks. Native lock ownership pins the descriptor after its source wrapper closes; failed unlock poisons future attempts instead of claiming positive busy. The adapter has no registration into runtime admission, arbitrary-path opener, rename or deletion primitive.

Darwin ARM64 creation uses a tiny compiled fixed-signature C bridge for variadic `openat`; ordinary managed mode arguments would use the wrong calling convention. Source-built universal Darwin assets are copied through project references and packed under both Darwin RIDs. The source/build/verifier/binary manifest rejects missing or mismatched inputs. Consumers need no C compiler. The universal x64 slice does not qualify the managed Darwin x64 adapter, which remains explicitly unsupported.

Root reviewed all changes and verified all 20 independently reviewed file hashes. The self-review loop stopped clean after five review/fix passes: static interop/ownership/packaging corrections, two preserved build invocation failures, a loader correction, and final verification. Copilot/Greptile were waived by the maintainer; neither was requested.

## Local gates

Commands ran serially through the build-slot wrapper with the program-owned NuGet cache. Each captured source/project/native/workflow manifest remained unchanged during its gate.

| Gate | Result | Log SHA256 |
|---|---|---|
| `native-core-build-v1` | Release core build, net8.0/net9.0/net10.0; zero warnings/errors | `a564c98f36b5f62df955a4b011d81f9f78f17a3c3dd3b600463c5a66db4bbd74` |
| `native-store-restored-v5` | Store after restoring the native mutation: 127 passed, zero failed/skipped; seven actual Darwin native cases | `bcbdff79d8c370b0a7fcb4f625b21d30175672b097e80ad29c5a61846cf4cf0d` |
| `native-integration-v1` | Integration: 160 passed, zero failed/skipped | `41562c7a87c1bddb6a9096b843afa27ec6a818e5ea5e6b97b88bc8ff5116c44b` |
| `native-pack-v1` | Local audit package created with both Darwin RID assets and all three managed targets | `8d66ff132eafb57ed1d2256f390d006029c939e26ef429a93f637a1f29d795cf` |
| `native-pack-stale-negative-v1` | Expected rejection of mismatched binary hash; no package emitted | `d8469b864162c8e31efa7ef20c5771f9bd0a21c3b4477c6c3743343f1bdbf839` |
| `native-pack-missing-negative-v1` | Expected rejection of missing artifact; no package emitted | `d00e73c7f4b9cfcf65763562f8325c4deb566226efbc4b42345fc9fdebfa3cb8` |
| `native-mode-mutation-v1` | Expected one test failure: actual native mode None rather than user read/write | `f7ea64ced928bf110724d9fa4d7145088d9154e41e46db8b2b20377edc50b9ff` |

Exact commands and raw logs are retained in the program's local `prune-admission-authorization-audit/native-filesystem-gates/` artifacts. Principal commands:

```text
dotnet build src/Nuplane/Nuplane.csproj -c Release --nologo
dotnet test test/Nuplane.Store.Tests/Nuplane.Store.Tests.csproj -c Release --nologo --logger trx
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj -c Release --nologo
dotnet pack src/Nuplane/Nuplane.csproj -c Release --no-build --nologo -p:PackageVersion=0.0.11-audit.native -o <owned-audit-output>
```

The audit package was inspected as a zip: net8.0/net9.0/net10.0 assemblies are present; both `runtimes/osx-arm64/native/` and `runtimes/osx-x64/native/` contain the exact verified binary SHA256 `7ff321363915e2dc0bdc077dbf9893628a96a80e76be94c15dd3c7da184990cd`. Package SHA256: `904b4bc3c009d8d73b1ff99da79384e8986fc1033e55b1726050dc32cf6c4e4a`. Two concurrent identical native builds into one owned output both passed and produced the same binary. No package was published.

## Failures and negative controls

The first two runs failed before managed compilation at the redundant `lipo -verify_arch` command; those logs remain preserved (both SHA256 `19d5fb19f6adc63c306ede93f1ef62c07ba85888b501da76c41f4a443b954273`). Architecture verification now uses the tracked Mach-O verifier already required before publication. The third run compiled but failed four native cases (123 passed, zero skipped): omitting `LC_UUID` made the macOS loader reject the library. Its log SHA256 is `f115ebd4b5ab6b48e6a77bcf84b06b12cab816f7fdc480ab848a9efd52066875`. The corrected build retains the linker's default content-derived UUID; actual runtime loading and repeated-build equality passed.

A temporary C mutation forced file creation mode to zero. The focused mode test failed at its value assertion (expected user read/write, actual None), rather than failing compilation or timing out. Original source SHA256 `614a99c2894a5534c24c6e7f6db61f3f5001e173810d4dd92b3a047c2a296b43` was restored byte-for-byte and all 127 Store cases passed again. Separate missing-artifact and mismatched-manifest pack commands failed before emitting packages.

## Provenance and remaining qualification

The accepted 711-input Store/build/pack manifest SHA256 is `bac6eb2768613e8244db9fbda5132aa99aab9951efcf85596da9560996352313`. After those gates, only `validate.yml` changed to add Linux package-asset verification; root and the independent reviewer reviewed that delta, YAML/XML parsed, and the Integration gate captured the final workflow. All production/native/test bytes remain identical to the accepted Store manifest.

Independent review SHA256: `d7fe279e7cc6abb31c6064351ede1f67c8fa22b6328c03ee9de310d209c146b9`. Review file-manifest SHA256: `d6ea78fd289b82947cca0ac5188fbe9b2d064ea1811009277ea381fc8bc8394c`.

The workflow now builds the Darwin artifact from the candidate checkout before Ubuntu build/pack, checks exact package asset hashes on Ubuntu, and requires seven macOS ARM64 native cases with zero skips. These hosted jobs remain pending until an exact-head run completes; local proof does not establish Linux or Windows runtime behavior.

The adapter is a mechanism beneath admission. Directory creation/reopen observations assume serialized cooperating writers and cannot prove atomic create-and-open against arbitrary nonparticipants. File `fsync` does not claim directory-entry or power-loss durability. Windows, actual root/state-slot case policy, component-wise alias authority, multi-state active/LKG enrollment, before-read graph leases, state replacement, inventory/quarantine/deletion and full supported-platform tests remain open. Held-parent replacement/verified cleanup and inventory enumeration need separately reviewed narrow operations. The two-composition non-destructive before-read gate still precedes recursive-delete implementation. Stable releases and final Foundation adoption remain downstream work.
