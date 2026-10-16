# Final native platform acceptance

Status: **accepted for Nuplane 0.0.12**, source-qualified at `3f8f2842060423420f26587aef62bacefbf71898`; the merged `main` commit `80f74d02bef70a0410f9dd21064692bc088bb87c` has the same qualified tree `90643f0bc6bebe3c4b8da78a17d5b2175a695b98`. This record covers actual owned-store, native filesystem and child-process gates. It does not claim customer-store mutation, a downstream host qualification or Foundation adoption.

## Hosted source and run

[Validate 38835166221](https://github.com/valence-works/nuplane/actions/runs/38835166221) completed successfully on 2026-10-16. All seven jobs passed: Release build and serial tests, five native profiles, and the universal Darwin shim build. The independently checked source archive contained 375 expected pins; all 375 matched, with no missing or mismatched pins. The archive SHA-256 is `9b14e84fc1013ad2547a8617332cc1067019ae69f09cd20aafb7ab28a6480c9d`.

The six serial test TRXs completed with 2,749 passed, zero failed and 70 explicit platform/capability skips. Their per-project counts and hashes are in [test evidence](tests.md). The 24 production project/framework outputs built with zero warnings and zero errors; see [build evidence](builds.md).

## Five native profiles

Every profile passed the same five strict manifest sets without skips: retained loading **83/83**, pending group admission **3/3**, partial execution recovery **14/14**, package-store enrollment **14/14**, and native multi-root group **137/137**. All other required native summaries in that profile also passed without skips. Platform-inapplicable steps were skipped by the workflow; those skips are recorded by runner and were not credited as native proof.

| Native profile | Job | Runner/filesystem profile | Strict sets (each no skips) | Verified manifest summaries | Raw terminal log SHA-256 |
|---|---|---|---|---:|---|
| Windows x64 | [State persistence](https://github.com/valence-works/nuplane/actions/runs/38835166221/job/116762972247) | `windows-latest`, x64, NTFS | 83 + 3 + 14 + 14 + 137 | 53 | `b8aebf8176059ad9f42df497e7fcfa2ace226d8d32b0a96593b4c792d37c8e37` |
| Linux x64 | [State persistence](https://github.com/valence-works/nuplane/actions/runs/38835166221/job/116762972279) | `ubuntu-latest`, x64, ext4 | 83 + 3 + 14 + 14 + 137 | 52 | `dbd96252d87b9893810649f526da7fc36da0dddcd2f4977916b0eb4e1efc0afe` |
| Linux arm64 | [State persistence](https://github.com/valence-works/nuplane/actions/runs/38835166221/job/116762972306) | `ubuntu-24.04-arm`, arm64, ext4 | 83 + 3 + 14 + 14 + 137 | 52 | `91b9672084f1d734fe92fac788dc23e41f2df33adefa78f5119a92acc403bca4` |
| macOS arm64 | [State persistence](https://github.com/valence-works/nuplane/actions/runs/38835166221/job/116762972334) | `macos-latest` / macOS 26 arm64, APFS | 83 + 3 + 14 + 14 + 137 | 53 | `68b0aa8eb62cd765cd3bc30a6b92f06ba167c6c7ebccbbb5036af206b00cd0ab` |
| macOS x64 | [State persistence](https://github.com/valence-works/nuplane/actions/runs/38835166221/job/116762972404) | `macos-15-intel`, x64, APFS | 83 + 3 + 14 + 14 + 137 | 53 | `5ccfd89b0d3d5e5c59177e4ecdb86d40161cb863916f462ea20786f0eeb1ce78` |

The Linux profiles also qualified owned ext4 case-fold volumes. The Darwin profiles ran the Darwin stat ABI and native store gates. Windows ran the native NTFS, state-destination, identity and deletion gates. The runner skipped only steps that did not apply to its OS/architecture, plus failure-only diagnostics after success; the successful logs record each step and its reason.

## Windows final guards

The terminal Windows job passed every final guard TRX with zero failures and zero skips:

| Guard TRX | Passed | Failed | Skipped |
|---|---:|---:|---:|
| `guarded-native-state-writer.trx` | 21 | 0 | 0 |
| `state-slot-write-guard.trx` | 17 | 0 | 0 |
| `native-entry-removal.trx` | 12 | 0 | 0 |
| `native-deletion-manifest.trx` | 13 | 0 | 0 |
| `native-windows.trx` | 21 | 0 | 0 |
| `identity-windows.trx` | 6 | 0 | 0 |

The broader native profiles also ran their platform-qualified identity, state-file, authority, admission, resolution, package-install, graph-use, enrollment, quarantine, deletion and process-recovery manifests. In particular, group publication was exercised by an owned publisher process killed at IntentPublished, StageFlushed and StatePublished, followed by fresh catalog-backed `AddNuplane` admission. The first two boundaries refuse before package callbacks while preserving ambiguous/incomplete evidence; the published-Next boundary recovers exact state and retains the explicit schema-2 runtime refusal. All three cases passed without skips on all five profiles.

## Scope and release boundary

These results qualify supported native profiles and the exact source tree used by Nuplane 0.0.12. The selected `UseLastKnownGood` policy protects the currently selected Active/Recoverable graph closure; it does not select unlisted historical-only recovery entries, which are explicitly refused without mutation. Ordinary nonparticipating writer compatibility remains outside the coordinated enrolled-store contract.

Nuplane 0.0.12 has been published. The separate package-family consumer, CShells serving-generation and Foundation pin/host adoption gates remain independent program work; this document does not mark them accepted.
