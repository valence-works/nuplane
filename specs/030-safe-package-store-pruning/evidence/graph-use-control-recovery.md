# Graph-use control-artifact recovery coordinator

This bounded T115 prerequisite recovers native graph-use control artifacts only. It neither removes package installs nor changes membership, active/LKG state or persistent protection. The internal caller must retain a verified Complete root and every member lock throughout the call. Runtime caller wiring and actual child-process crash/restart acceptance remain open; T115 is not accepted by this increment.

The implementation shares the bounded canonical namespace parser with ordinary inspection. Inspection remains read-only and refuses incomplete pairs. Recovery first captures and validates the entire bounded namespace and every published record/install closure, then replays all plans before its first mutation. Unknown names, duplicate phases, malformed/digest-invalid published records, mismatched epochs/identities, ambiguous absence and changed native paths preserve evidence and refuse.

| Exact evidence | Recovery behavior |
|---|---|
| Published JSON and sentinel | Probe ordinary sentinel ownership; a live owner is preserved. After a stale probe closes, acquire a fresh native removal token, replay the record/closure, journal through `reaping` and `deleting`, then remove the exact sentinel. |
| `reaping` or `deleting` and sentinel | A live owner restores canonical JSON without replacement. A stale owner resumes from the exact phase under fresh removal authority. |
| `deleting` and positively absent sentinel | Acquire a fresh token for the exact marker; read and revalidate its complete current bytes/digest/closure through that token before removing only the marker. |
| Empty/truncated `json.stage` and sentinel | Stage bytes are bounded evidence, never graph authority. A stale sentinel can advance the exact stage to `stage-deleting` before removal; a live owner preserves the pair. |
| `stage-deleting` and sentinel | Resume the exact unpublished journal under fresh authority. |
| `stage-deleting` and positively absent sentinel | Compare the exact bounded current bytes through a fresh marker token before removing only the marker. |
| Empty sentinel alone, every companion positively absent | Ordinary liveness probe followed by a fresh token and complete namespace replay permits exact sentinel removal. |
| Plain JSON, `reaping` or plain stage without its sentinel | Refuse; absence does not infer a successful prior phase. |

An ordinary read/write sentinel probe precedes removal-token acquisition because actual Windows publisher handles deny DELETE sharing. Positive busy ownership never authorizes a removal-token attempt. A prior Stale result or diagnostic process ID is never mutation authority. Native tokens retain exact parent/name/file identity, lookup semantics, regular single-link kind and ownership through removal and final absence verification. Token-aware metadata and bounded detached-byte reads use that same retained handle; Windows never reopens a DELETE-capable marker through an incompatible ordinary handle.

Root and independent review of worker coordinator `f785ac1` found a terminal-marker byte-replay gap: same-identity, same-length bytes could change after prevalidation and before the fresh token. Correction `3128a62` closes that gap for both published and stage terminal markers. Published bytes must equal the saved fully validated payload and pass current root/epoch/use-ID/digest/sentinel/install validation; stage bytes must match exactly. Positive sentinel absence is replayed adjacent to removal. The portable regressions mutate before token acquisition, so Windows sharing restrictions cannot mask their causal assertion. Bypassing both byte comparisons makes both tests fail; byte-exact restoration makes both pass. A separate causal control proves actual live publisher ownership prevents any removal-token attempt.

Independent correction review SHA-256: `e0bb5c2b9de0854c0d8416e95a16de73182af5f868349e3a688990282cd83a0b`. Frozen correction bundle manifest SHA-256: `d04c4edd59664a6776b6334acf96e34ff5578d3a4cdb8aff3e38892a4f00568d`. Root matched all eleven integrated coordinator/native/test files byte-for-byte against final worker commit `3128a62`.

## Verification boundary

CI preserves the existing 34 publication/inspection/binding/acquisition cases by explicitly excluding the new recovery methods from that gate. A separate exact-name manifest requires all 26 recovery-phase cases. Native control recovery now requires eleven cases on each platform, including same-token inspection and bounded-byte ownership. Every required case must execute and pass; empty, missing, duplicate, extra, skipped and failed accounting controls are rejected. Windows manifest selection on a macOS TRX is accounting evidence only; actual Windows execution is required separately.

Final integrated local gates passed:

| Gate | Result | Log SHA-256 |
|---|---|---|
| Existing publication, new recovery and native control selections | 34 + 26 + 11 passed, no skips | `11a6eacba3a27c1aedc716ec479721eb8256fcd65d9d04b4ca7d4378dabea36b` |
| Full Store | 739 passed, 0 failed, 42 explicit platform skips | `f93b29ecc9d3ebd4727471575c180b3a959b1d142c07db3f922580dfec32eae8` |
| Core Release .NET8/9/10 | zero warnings/errors | `fe5d07b4041601535b37fa108e5998c37153ea311739b0fff579ac0e4b2e6b41` |
| Full Runtime | 878 passed, no failures/skips | `0e46c02650a1b44a804a6630dcaf1ff94568fbcee7ad87547ab5314e58de23b4` |
| Corrected full Integration | 212 passed, no failures/skips | `161c4ef412a436818ff239431f0bb46f7d0d8119a6d0fb1fbf8225d7ea7cea9b` |

The final Runtime, corrected Integration, focused CI selections and one-case restore compatibility proof used unchanged 959-input manifests, SHA-256 `d73695ee609eb477fc9f5b965fe612b7587e046450c954b40288817083560d6e`. Earlier passing Store and Core gates used manifest `fb31ada7da0a12410d770cb37a2169768e5764dfbd9183a5a961dbca0093ea76`; the sole input delta is the existing Integration test correction described below. Production, Store tests, workflow and required manifests are unchanged between those gates. All three changed Bash blocks parse; accounting rejects eighteen negative controls across the recovery and Unix/Windows control manifests. Windows selection checks remain accounting-only until hosted execution.

The first integrated Integration run had 211 passes and one failure: its host-free no-loading test compared the global load-context count before/after restore, and an unrelated collectible context retired (8 became 7). Package-assembly absence passed. The test now compares surviving context identities against the before set, preserving package-assembly absence and rejecting new contexts while allowing unrelated retirement. The corrected focused case and full 212-case suite pass. The original failed log and TRX are preserved, not relabeled as successful.

Independent integration delta review SHA-256: `eeede0195c4b41ce2876f6d1a6a6a706b6fce2c70873bb9f817bf8cb0cbab721`. It verifies exact gate selection, bounded T083 acceptance, all eleven source pins and the narrow Integration test correction. Actual hosted Windows/Linux qualification of this recovery candidate remains pending; the earlier `0579157` platform run qualifies PackageContent and does not qualify these new recovery gates.

The complete all-driver/earliest-read gate, multiroot offline snapshots and Loading ownership, runtime recovery wiring, actual process-crash proof, public maintenance, native package deletion/recovery, stable releases and Foundation adoption remain required. No recursive-delete implementation or executor tests have started.
