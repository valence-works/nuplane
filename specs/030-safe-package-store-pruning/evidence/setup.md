# Setup acceptance — 2026-10-09

T001–T005 are root-reviewed and accepted on local macOS arm64, .NET 10.0.300. This accepts isolated test infrastructure only. No production source, package-store admission, context lifetime, crash recovery or physical package deletion is accepted by these results.

- Shared temporary fixture: unique package root and state slot, relative-path confinement, final/intermediate link refusal, owned nested cleanup, idempotent disposal and sibling-sentinel preservation. Linked into Store.Tests; other projects link it when they consume it.
- Real non-packable child executable: finite JSON-line ready/release gate, with PID validation and bounded owned termination. It does no package IO. Actual Nuplane crash/read points remain T114 work.
- Root owns launcher/project-output wiring and performed integration review; independent reviewers examined the fixture and child harness, and root verified the reviewed source hashes. No Copilot/Greptile request was made.

## Executed gates

Both commands ran serially through the normal machine build-slot wrapper with an owned NuGet cache. All 648 captured source/project inputs were unchanged during each command; result manifests and raw logs are retained in the delivery workspace's `prune-admission-authorization-audit/setup-gates/` artifacts.

```bash
dotnet test test/Nuplane.Store.Tests/Nuplane.Store.Tests.csproj --configuration Release --filter FullyQualifiedName~PackageStoreFixtureTests --disable-build-servers -p:UseSharedCompilation=false
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj --configuration Release --disable-build-servers -p:UseSharedCompilation=false
```

| Gate | Passed | Failed | Skipped | Raw log SHA-256 |
|---|---:|---:|---:|---|
| Store fixture tests | 10 | 0 | 0 | `f43c85190be932af11c9eda8fe749daf8bd5c5147fbb1f1f34307ac965285126` |
| Full Integration project (including four child-process tests) | 160 | 0 | 0 | `e91d15b19afb9a17a4df924d6172f8cb848d1d1542200a900561c7b2eac23246` |

The child smoke tests prove actual ready/release exit, disposal termination/idempotence, cancellation before release and cancellation before start. The fixture link test does not skip unavailable privileges. Windows/Linux runner proof is pending; no cross-platform acceptance is inferred from this macOS run. Build output showed no warnings/errors. Product net8/net9/net10 builds remain required once production source changes.
