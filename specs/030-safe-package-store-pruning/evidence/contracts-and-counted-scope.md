# Protection contracts and counted operation scope

Accepted 2026-10-09 against base `32c11e37945c00653cd58300ef2ae2cff41e36a2` and the frozen source-input manifest below. This is a foundational increment, not native admission, runtime integration, pruning, cross-platform or stable-package acceptance.

The neutral authority handles and validated graph snapshots have internal constructors with friend access only for core Nuplane. Public physical/install values remain descriptive. Optional companions preserve existing required interfaces; both Loading contexts retain their positional constructor/deconstruction and copy lease views. Core counted state rejects new borrows during close, drains existing borrows and in-flight validation, then releases the already-held primitive exactly once and shares faults. No runtime registration or package IO driver changed.

Root reviewed all production/test changes, verified 44 independently reviewed file hashes and all 691 source/project inputs. Independent static review found no material blockers. Root owns integration and QA. No Copilot/Greptile request was made.

## Gates

Commands ran serially through `/Users/sipke/.local/share/dotnet-build-slots/bin/dotnet` with an isolated NuGet cache. All accepted runs retained identical source/project inputs.

| Gate | Result | Log SHA256 |
|---|---|---|
| `contracts-state-build-v1` | Release build of Nuplane.Loading plus its three changed production dependencies, all net8.0/net9.0/net10.0 targets; zero warnings/errors | `2aa90009cddadbacf5cde8a22e610f36f28c622762705d244a760923b36101f4` |
| `contracts-state-store-restored-v2` | Store after mutation restoration: 102 passed, zero failed/skipped | `d960af2b0f7cb92dc2866e6a6e62bad443183c10dfd9b3e9d1fba6420782d513` |
| `contracts-state-loading-v2` | Loading Release: 257 passed, zero failed/skipped | `92725b4ab85331b7864c2844cae8e96bc5bb88d0d86e4f5229b90212c9566765` |
| `contracts-state-runtime-v1` | Runtime Release: 799 passed, zero failed/skipped | `0eefc840c31b81dfee9ce838d3567d922dbbcdac4b46e2a76aad3da173979354` |
| `contracts-state-integration-v1` | Integration Release: 160 passed, zero failed/skipped | `caba3769e60a271be61ab0f376318f2bf5b7bcb6890ae9d7d2d6854a7b600ccf` |

Exact commands:
```text
dotnet build src/Nuplane.Loading/Nuplane.Loading.csproj -c Release --nologo
dotnet test test/Nuplane.Store.Tests/Nuplane.Store.Tests.csproj -c Release --nologo
dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj -c Release --nologo --no-build
dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj -c Release --nologo
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj -c Release --nologo
```

The initial Loading Release run preserved two failures (255 passed, two failed, zero skipped): pre-existing tests require the Conflict fixture DLL specifically under `bin/Debug/net10.0`. Preparing it with `dotnet build test/Nuplane.Loading.Tests.Fixtures.Conflict/Nuplane.Loading.Tests.Fixtures.Conflict.csproj -c Debug --nologo` made the unchanged Release suite pass. The failed log hash is `52b325cceb2d76b12786504d93f64704e85224cb269b0df59b324742a2edda8e`; it is not represented as a pass.

## Behavioral negative control

A temporary mutation ignored outstanding borrows when choosing the initial close-drain task. The focused outstanding-borrow test failed at its early-release assertion (one failed, zero passed/skipped), rather than timing out. Mutation log SHA256: `e9b3e76f3e405438c6f451f00afd5d9eb59d5d99316daa02b148c77a15002afe`. The original source was restored byte-for-byte (`74be9f5b98fdfebd0b0783179a530baa8494d32c62f118e297794d08ad976d94`), and the full Store suite then passed. This proves the unit test detects that unsafe counting behavior; it is not native locking proof.

## Provenance and remaining work

Source manifest SHA256: `923a437ad8f3ee229db1b36f6ddfb0afd6f7ca1d8f61ba5700f0911a38e221f1`. Independent review SHA256: `e3da2f29771ef4b9ad32d0dc71308f7ade71d26e6abd90cae518dc5623e6551d`. Review file-manifest SHA256: `5258f3c87b21e4fd9935a3bb8e63a7605e7e379a4c087db30638e189c4ad513d`. Raw logs, exact commands/input manifests, mutation and root-verification records are retained locally under `prune-admission-authorization-audit/setup-gates/` in the program artifacts.

T009/T011 persisted records/publication companions, physical admission/enrollment, every reader, actual collectible/noncollectible lifetime, native root ordering, safe manual execution and supported-platform evidence remain open. T045 is partial; the counted-state test portion alone does not accept native ordering. The real two-composition before-read gate still precedes recursive-delete implementation. No package release or final Foundation adoption is claimed.
