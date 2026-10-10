# Multiroot ownership and durable protection

Root-selected protocol invariants, 2026-10-10. This extends [admission](admission.md) and the [data model](../data-model.md); it records required behavior, not implementation acceptance. The v2 encoding and producer/recovery implementation remain pending. V1 one-root behavior and positional public contracts remain compatible.

## Ownership prerequisite

Actual multi-path admission must acquire every distinct physical root lock in canonical root order **before the first member lock**. Reread every expected ledger under all root locks, derive its complete member-lock set, then acquire every root-local member lock in deterministic state-slot/root order. Recheck ledger digest/file identity and replay configured locators after the complete lock set, before any member payload read.

Member lock files live in each root's control directory. The same `StateSlotIdentity` in two roots therefore requires both physical member locks; payload operations on one exact physical state slot may be deduplicated only after proving that binding. Current contention is fail-fast; the previous per-root root/member sequence is an ordering mismatch, not a demonstrated blocking deadlock. Retain one union lock lifetime until every root-specific owner's counted borrows/access pins drain, and unwind all partially acquired resources in reverse on failure or cancellation.

## Persistent participant boundary

A logical persistent state has one exact physical state slot acknowledged by every participating root. Existing independent states remain independent logical members; matching state-body digests do not establish member equivalence. Replicated state files are not implied by this contract.

Select the complete trusted enrolled participant superset during explicit quiescent enrollment. It must cover roots used by both Active and recoverable/LKG closures and stays fixed across ordinary generations, including KnownEmpty ones. Adding/removing participants requires the existing explicit quiescent cutover over the complete old/new union; a root cannot disappear merely because the newest Active graph moved elsewhere. Persisted physical identities are comparisons, not path-open authority; resolve/replay participant locators through native authority discovery before granting a group owner.

The state needs an additive root-keyed protection envelope preserving its existing positional constructor and legacy protection property. Each root has its own epoch, member identity, revision and acknowledged file identity, while the envelope binds one logical member, exact common state body, common generation and complete participant set. Local root revisions are not a cross-root generation clock. Recovery-selection source evidence must explicitly bind the common generation under the new schema rather than silently treating one root's local revision as a global counter. Missing/duplicate/unknown rows, unverifiable legacy LKG and unsupported serializer round-trip remain Unknown/Incomplete; never truncate a graph or infer KnownEmpty from absence.

Every row carries the complete descriptive Active/LKG graph closures. Root-local native verification and maintenance observe only that root's nodes; a retained read spanning roots must separately validate the complete participant union. Live graph-use records protect process-held lifetimes and never substitute for durable Active/LKG acknowledgements after process death.

## Monotonic group publication and recovery

Acquire all roots and all member locks before publication. Persist and verify pending/Incomplete intent in **every** participant before replacing the shared state payload. Prepare, publish and reopen the exact common state/envelope; verify every root-local install binding and row. Persist and verify the same irreversible group Next resolution in every participant **before the first Complete Next acknowledgement**. The resolution binds the participant set, transaction, prior/next generations, state/bundle digests and exact prior/stage/backup identities.

Once any durable Next decision or Next acknowledgement exists, recovery cannot select Prior: a Complete Next root may already have retired Prior-only installs through root-local maintenance. A prefix of resolution markers has no Complete acknowledgement and must be finished only from exact group evidence or remain Incomplete/Unknown. A prefix of final acknowledgements is safe only after all matching Next decisions were verified. Recovery reacquires the entire trusted union; unavailable, missing, third or conflicting evidence preserves artifacts and refuses completion. Keep prior evidence until the durable group decision and identity-bound cleanup permit its removal. This is process-crash recovery, not multi-file atomicity or power-loss durability.

Before a v2 payload can become usable, all existing state writers must preserve and update its complete envelope under the group owner or refuse **before replacement**. A single-root borrowed writer cannot acknowledge only its row. This includes failure and source-snapshot mutations as well as active-state publication, copying and custom serialization.

## Acceptance boundary and evidence

The current single state-protection property and local pending/ack protocol do not satisfy this multiroot contract. The [ownership prerequisite](../evidence/multiroot-admission-ownership.md) is integrated locally with passing affected-suite evidence and independent review; hosted platform qualification remains pending. The v2 payload/publication slice follows after its exact encoding and recovery contract are selected. Do not accept offline snapshot, Loading, full driver or pruning tasks from lock mechanics alone.

Required proof includes real two-root ownership at the first member lock and before the first payload, distinct root-local lock obligations for an equal slot, borrow drain, contention/cancellation/identity mutation and a compiled old-order negative control. Durable publication additionally needs real root-local epochs/revisions, identical common generation/body, actual state/ledger reopen, and fresh-process interruption after first intent, all intents, state replacement, prefix/all group decisions and first Complete acknowledgement. No crash point may expose Complete stale Prior after Next became irreversible. Unavailable/mismatched participants remain Unknown. Every driver still needs its own before-read proof before the mandatory non-destructive gate permits recursive deletion.

Source audit at `6b817fcbc91722802102a2cea38091093e377fc1`: SHA-256 `36c5ebbe194453e201ff5b9e8dcdf9d84c0c9068f8745f538f1ce929c647d1d5`; independent architecture review `ddfd77719c6fa934d1a08fc35f92e90d391dc60df86396730636ec8f54f5766a`; selected ownership contract v2 `e37e2b306c72e9c1369011ec3fb3af88ccd292974065a7ba425d547f5face7f9`. These are bounded source/protocol reviews, not executed producer or restart-test evidence.
