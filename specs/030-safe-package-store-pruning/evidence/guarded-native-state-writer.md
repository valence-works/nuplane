# Internal guarded standalone state writer

This increment implements an internal writer for an already-resolved held parent and exact state slot. It takes the supplemental slot guard, proves permanent group-marker absence before reading prior payloads, and retains the guard through native publication and outcome verification. The retained-path callback validates ancestry and parent evidence without pinning the replaceable final file identity or acquiring root/member locks.

The writer rejects incoming/existing v2 bundles. It streams ordinary payloads without the ledger's 4 MiB quota, preserves original prior bytes in a verified backup, and reopens staged and committed payloads through the selected codec. A final prior-byte hash check follows awaited callbacks and precedes synchronous native verification/publication. Cleanup requires the captured prior identity and bytes or positive initial absence; changed evidence is preserved and refused.

A publisher exception after replacement is classified from actual destination identity and bytes. Verified commits remain observable when backup removal or guard release reports an error. Unknown destination/path/marker evidence refuses with recovery evidence retained. This establishes no power-loss durability guarantee.

## Local verification

Root ran the gates serially on macOS against the same frozen executable inputs:

| Gate | Result |
|---|---|
| Focused native writer suite | 17 passed, zero failed/skipped; unchanged strict exact-case verifier passed |
| Full Store Release/net10 suite | 849 passed, zero failed, 44 explicit platform skips; 893 total |
| New cases in the unmodified full-suite TRX | All 17 appeared exactly once and passed |
| Core Release build | net8/net9/net10, zero warnings/errors |

Omitting only the final prior-byte comparison made the successful-codec/same-inode mutation regression fail because publication incorrectly succeeded. Root restored the exact production bytes before the final full Store/Core gates. An earlier test-adapter compilation failure executed no tests; a subsequent focused run had 16 passes and one incorrect callback-count assertion, corrected to require earlier marker refusal. Those attempts remain preserved. The unchanged Store suite retains its existing `RootMembershipRecordTests.cs:79` xUnit2031 warning.

Frozen artifact hashes:

- Executable-input manifest (1,025 files): `6e0a618c8c693ef39c6cb59a862a8d86fa73db288152755cde76ae63befd8d41`.
- Local qualification: `d5d16fd8cd1cff0f24fa9ab13e50205b21414b3bc661f7dd6293962258109de3`.
- Full Store TRX: `c2aeef9f5a4d6e2c1c416abace253a54a8ed7baeb7f7f333a829a3cfb902dbda`.
- Core log: `0af6c16acb556220d873196871d4e43e3c5ec78489b8813fca0055436b6326f3`.
- Independent final review: `b729ac78d39b4a89fb5d113d3b49c839a772e6e1e290eea890c075f01229f5f7`.

Root and independent review found no remaining actionable issue within this bounded increment. The workflow now selects all 17 cases on its existing four native platform lanes; execution against the newly published source remains pending. Earlier hosted group-guard results do not qualify this delta.

## Remaining boundary

`StoreStateSerializer.SaveAsync` is not routed through this helper. Default path resolution, missing-parent creation, ordinary platform compatibility, runtime wiring, complete authoritative first-read/restart fencing, producer activation and physical pruning remain open. T034/T116 remain unchecked and the overall checklist remains 38/128. No stable release or final Foundation adoption is proved here.
