# Locked protected-state publication and enrollment completion

This increment implements the internal one-physical-root completion boundary. It does not grant runtime admission, enroll a running composition, implement a public enrollment service, or authorize deletion. T029, T060 and T063 remain partial; the complete program remains open.

## Actual operation

`LockedMemberLocations` now reads and publishes member state under its existing root/all-member owner. It revalidates the entire locator/ledger union before and after state reads. Publication expires the previous location scope, uses the same transaction without reacquiring its own locks, then replays every member location against the newly acknowledged ledger/native file identities. Interrupted publication expires the context and retains durable pending evidence for fresh recovery. Declared members cannot be read; prospective members have no payload.

`CompleteEnrollmentAsync` requires explicit continuing quiescence and freshly acquires root plus every existing member lock. It requires exact Incomplete/non-pending membership, acknowledged members equal to the non-empty target set, and root/epoch agreement. It then rereads every actual state file through its bound native slot. The semantic verifier checks body/protection/ledger digests, exact acknowledgement/revision, active descriptors and activation records against complete graph snapshots, dependency ranges/reachability, and the versioned startup fallback selection. Each active version must equal its LKG entry; unselected LKG keys alone are allowed because current normal persistence retains them. Unknown legacy recovery is refused, never silently cleared.

The operation observes both active and recoverable installs using held native directory/completion identities, replays every descriptor's absolute path componentwise, and compares the resulting native directory identity with its protected install. It revalidates the entire member/install union before publishing Complete under retained ownership. Supplied archive hashes are descriptive here; this boundary does not recompute archive contents. A cross-root protected graph refuses rather than pretending this one-root verifier supplies multi-root ownership.

The returned verification dictionary is descriptive. A caller cannot retain it as an admission capability after releasing ownership. Ordinary configured authority resolution continues to refuse Incomplete enrollment; only the private locked verification scope can traverse its own Incomplete authority.

## Producer-to-completion proof

Four native facts use two distinct persisted state files and one physical package root. They run actual InitializeIncomplete → BindDeclaredMembersAsync → two publications through one locked owner → fresh Complete verification/publication → locked Complete reopen and full reread. Graphs come from the actual dependency resolver and catalog mapper; install identities come from the native reader and snapshot factory. Both members protect the same actual dependency directory/completion identity. These are not hand-authored Complete-ledger fixtures.

Negative cases refuse changed completion identity, a descriptor pointing to another native install, unresolved legacy recovery, digest-valid state with unselected graph versions, absent quiescence, unacknowledged membership, malformed actual state and an offline member before the callback. Separate owner tests exercise two successive publications without recursive lock acquisition and context expiry after pending interruption.

Two intentional controls removed (separately) the native install verification call and the semantic state verification call. Each relevant regression then failed because unsafe completion no longer threw. Both controls were restored byte-for-byte to production verifier SHA `b1fd62e291238ba51165183c05c2de96097f3f9f066c1873ee24a25d77f893af` before the final green gates. Control log SHAs: native `ac0731f19aa8a284b7cf94b4030d97da690b1a22b972360e7290741bc003f227`; semantic `5eea48580cc7c7d996afe83db775241bd68d1a4e0427ee9e0c7d84d3df6484f8`.

## Root and independent review

Review corrected an overly strict active/LKG-map equality: normal persistence can retain unused LKG entries. It also corrected a pre-existing test locator basename that differed from its actual `state.json` slot, and made unknown next protection a typed refusal before pending construction. Earlier compilation/test failures are retained as failed evidence; they are not counted as qualification. Final root and independent source review found no remaining concrete blocker in this increment. Review is not runtime integration proof.

## Final local qualification

All seven final gates used unchanged source-input manifest `fc65e148e1ce087f8cfa6de0ffe0862f7c71c41eb1b96d316c722f1eacbbf6e8`. Logs, TRX and manifests are retained under `native-filesystem-gates/protected-completion-*-final-v1` in the owned program artifacts.

| Gate | Result | Log SHA-256 |
|---|---|---|
| Native locator/install/producer/completion | 29 passed, zero failed, two explicit ext4-only skips | `f47607bd11eab10a60716624f9486c812c8c0b4d55117dc4f804b6fb180d7d93` |
| Coordinated native publication/locks | 39 passed, zero failed/skipped | `e74eeb2418fa1bc40e2f49887eee6f206b4b304187a35acb42ac208cebdecb04` |
| Pure semantic verifier, net8/net9/net10 | 10 passed per framework, zero failed/skipped | See corresponding gate result manifests |
| Core net8/net9/net10 build | Zero warnings/errors | `7498b0e7cea82b1e34a322fba306d5e95b17dbfc754e227eb343f0e0517a68ed` |
| Full Store suite | 431 passed, zero failed, 30 explicit OS/casefold skips | `9e5a5f67114874d031e7b23434471e45f99f8f54716e55335209f6c153553470` |

CI now discovers exactly 31 native boundary rows: 22 uniquely required passing scenarios, six uniquely passing malformed inputs and three explicit platform outcomes. Ordinary Unix lanes require 29 passes/two casefold skips; Windows requires 28 passes/three platform skips. Both owned Linux casefold lanes still require all seven selected cases without skips. Coordination requires at least 39 passing rows, including both new owner cases. Pure semantics join the existing three-framework/four-platform protection matrix. All nine embedded Python validators compile. The actual local native/coordination TRX pass their validators; controls with missing, duplicate, skipped-required or unexpectedly passing casefold rows all refuse. Workflow triggers, permissions, concurrency and runners are unchanged. Exact new-head hosted qualification remains required.

The preceding candidate head `a27b1c71e73f64d01affa7a0f823f2f7ecb0a500` passed all six jobs in [Validate 37967709726](https://github.com/valence-works/nuplane/actions/runs/37967709726): Unix boundary 23 passes/two skips, Windows 22 passes/three skips, both owned ext4 gates seven passes each, full Ubuntu solution 1,708 passes/zero failures/30 skips. That proves only the preceding metadata/candidate scope. Its prior Windows expectation failure remains recorded in [member/install evidence](member-locations-and-graph-snapshots.md).

## Remaining integration

The mapper correction now reconciles successful root contraction and same-version failed-root retention; see [retained-admission and transition evidence](retained-operation-admission.md). T062 remains partial: if one successful root changes version while a failed root needs an old subclosure, selection/conflict checks must run before pointer transactions and protected publication. The verifier must not be weakened to accept inconsistent projections.

Still required: public quiescent enrollment orchestration and recovery/migration, configured and multi-path operation admission, complete protection on every runtime write, exact startup selector consumption, every reader/loader/restore/contributor/observer boundary, real two-DI-composition before-first-read proof, graph lifetime leases, safe manual preview/execute, upstream releases and Foundation adoption. No pruning eligibility, deletion, merge readiness or release readiness is claimed by these local gates.

## Exact-head hosted qualification

Commit `9e4dc0792d661b4c49bf49464f7f44865409a972` passed all six jobs in [Validate 37970406497](https://github.com/valence-works/nuplane/actions/runs/37970406497). Root parsed the complete logs and exact-head metadata: each Unix native boundary passed 29 with two explicit casefold skips; Windows passed 28 with three explicit platform skips. Each platform passed all 39 coordination cases without skips. All twelve platform/framework protection runs passed 97 cases without skips, including the ten semantic cases. Both owned Linux ext4 casefold lanes passed all seven cases. The full Ubuntu solution passed **1,726**, failed none, and explicitly skipped 30 OS/casefold cases; those native special cases have dedicated lanes. Full log SHA-256: `287e6b7f90389314cb445e91d1f82a4768369cb76ad46bbfdb8cba6526a9cfff`. This qualifies the internal owner/completion increment on the listed providers; public enrollment/runtime admission, leases, deletion, releases and downstream adoption remain open.
