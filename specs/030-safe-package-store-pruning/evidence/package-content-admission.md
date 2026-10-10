# Native-admitted PackageContent reads

This bounded increment preserves the two-argument directory/archive API for positively Unenrolled paths and adds explicit operation-borrow and graph-use-lease overloads. It does not complete the all-driver before-read gate or authorize recursive package deletion. Checklist remains 37/128 pending platform and complete acceptance review.

Legacy reads classify authority through native metadata before payload access. Enrolled, Incomplete or uncertain paths return null. A live borrow admits the exact directory or archive through the original counted operation owner; an exact graph-use read pin protects an extracted install represented by the immutable graph. Graph leases do not authorize arbitrary `.nupkg` artifacts. Archive reads retain the native parent, canonical basename and exact file handle. Missing content returns null; changed admitted targets and invalid authority raise typed refusal. Recoverable content I/O and invalid ZIP content return null consistently.

Directory reads retain and replay every native parent/name/child edge, lookup profile and reserved-authority absence before and after detached payload reads, including before missing-result returns. Links, hardlinks, special files, cross-volume edges and nested `.nuplane-store` authorities refuse. Extension lookup skips ordinary matching directories, continues to files, and replays the bounded enumeration and skipped directory identities. Archive member names reject traversal/absolute paths and ambiguous case-insensitive duplicates. No absolute-path payload reopen or new arbitrary content quota is introduced.

Root and independent review of initial worker commit `141b803` found missing ancestor replay and matching-directory compatibility. Correction `2afaa87` fixes both and adds nested-authority and archive Try behavior coverage. Independent final review found no actionable issue in production or the CI delta; SHA-256 `75280b5149f14a26cf38c0ff209aad4304c8f2bfe3da0d954d6c58372ec19d0b`. Root verified both frozen source manifests against exact git objects and all saved test evidence. Omitting post-read directory replay made the portable changed-native-edge regression fail with the expected missing admission exception; byte-exact restoration passed.

## Integrated local verification

All six root gates passed with unchanged 956-input manifests, SHA-256 `dbced2d71c8f2b7a89fccb1b9d24ff83bc6e12b7eb052a9dd9d363ad9ba3f651`.

| Gate | Result | Log SHA-256 |
|---|---|---|
| Required compatible content cases | 10 passed, no skips | `a431778b13b3c849d4a2467bae21384a44d5a41304f130c2a3e8e81998769928` |
| Required admitted native content cases | 15 passed, no skips on macOS | `9ac3193ecaac697b7ffdef70e560a5eb688628edae10febdee1d6f9a80ea0d4c` |
| Full Runtime | 878 passed, no failures/skips | `c74756d0279b007dcc68f3908204bc53b76efc4baf50e33eff2a21aaa709936e` |
| Full Store | 711 passed, no failures, 42 explicit platform skips | `f64207004de69d0f9943a79ed4c8f750afeb4d27ae8d4e3c30d6966e946b994b` |
| Core Release .NET8/9/10 | zero warnings/errors | `423b19512354023a68f75d5b3ef211557db4a6e64776a63656885c709833d0bb` |
| Full Integration | 212 passed, no failures/skips | `2d2b5544a39da32d765c3bb7466485da6728ca1770dea487dc24ba99e5137ca9` |

Root checkpoint SHA-256: `2fbce936bca8ae7eef5b9cf32c864ae2bf1a2c38f9b00f81975a3aeeb216eef4`. The changed Bash blocks parse. Exact-case accounting accepts the real 10/15 TRXs and rejects empty, missing, duplicate, extra, skipped and failed controls for each gate. CI requires 10 Runtime cases and 15 Store cases on Unix / 13 on Windows. Only the two explicitly Unix-only physical rename proofs are excluded from Windows; the portable changed-native-edge causal test remains required. Selecting 13 rows from the macOS TRX checks accounting only and is not Windows execution.

## Remaining acceptance

Actual Windows/Linux execution for this candidate is pending. T083/T101 and the complete before-delete gate are not accepted by these local results. The independently reviewed recovery coordinator still requires a terminal-marker byte-replay correction, caller integration and process-restart/platform proof. Remaining runtime publication, historical migration, startup-selector and package-access drivers, public manual maintenance, real pruning and recovery, stable upstream releases and final Foundation adoption/e2e/main proof remain required. PR112 is not merge/release ready.
