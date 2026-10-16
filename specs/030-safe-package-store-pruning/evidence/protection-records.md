# Protection and membership record evidence

T009 defines immutable descriptive candidates in the one-type-per-file `ProtectionRecords/` and `MembershipRecords/` groups under `src/Nuplane/Store/Coordination/`. These records grant no filesystem, admission, read or deletion authority. Serializer conversion, digest computation, durable publication/recovery and runtime integration remain their separate tasks.

## Representation and review

Active and recoverable LKG closures are independent tagged values. Unknown requires a reason and has no graph collection; Known always has a copied collection, whose explicit empty form is KnownEmpty. One snapshot identity may be both active and recoverable with identical complete payload. Versioned recovery-policy evidence keeps source revision and exact selected root-node IDs; future revisions refuse. Retired graphs require explicit snapshot/generation/epoch/revision/reason/proof evidence and cannot overlap protected graphs. The unresolved legacy-recovery marker remains independent of otherwise known collections.

Membership distinguishes Declared, Prospective, ExistingUnprotected and Acknowledged bindings. Prospective parent/profile/requested-name evidence exists before a canonical file slot; existing legacy state has exact slot/file/body evidence and explicit protection absence, with first protected revision 1. State locators may be outside the install root. Incomplete candidates retain explicit member unions and target IDs. Complete candidates require exactly acknowledged targets, the current epoch, known active/recoverable closures, no unresolved legacy recovery, and no pending publication.

Pending records bind the exact prior member and root/epoch to the complete next protection candidate and its checked incremented revision; no next file identity is guessed. Full nested protection payload comparison prevents equal supplied digest strings from hiding different prior evidence. Equivalent graph ordering is irrelevant, but duplicate occurrence counts remain significant. Known canonical slots are unique across member IDs, with no managed name folding.

Root reviewed both delegated slices and integrated fixes for a missing closure-copy method, a nested type/member name collision, missing ledger digest, future recovery evidence, legacy completeness, duplicate slots and digest-only prior comparison. A second independent source pass found no remaining actionable issue. All sixteen production files and two test files were pinned and their reviewed hashes verified. The final review report SHA-256 is `b5394a743ddb04a988e362f8e857033b0fa5aaad244316f34b4cb6678308d32e`.

## Verification

Final production builds passed .NET 8/9/10 with zero warnings/errors. The complete local Store suite passed **197**, failed **0**, and explicitly skipped **14** Windows/casefold cases. All **55 new record cases** executed and passed. Those local platform skips remain separate from the prior native qualification documented in [native identity evidence](../native-identity-evidence.md).

| Evidence | SHA-256 |
|---|---|
| Final all-TFM build log | `b0fb6da7ae158933a37106298cd63a0aefc74026833d1576b46d7dce7296fd57` |
| Final Store test log | `4aee74c1f66170fbe25ad5b7cc4568eadda5596a7cd96e6dee80c60abb685c4b` |
| Matching build/test source-input manifest | `a9906d83cfcc443eb78e09bfdff1559f55dfef7e1a058bc644c870bbe4778707` |
| Digest-only prior mutation log | `6a4133b6a34a5270f794c6ed8362c06bfc5e857b3e7e9bd772d1f7eff378447f` |
| Legacy-completeness mutation log | `8aaa4145c554a52dbd9df8f68f59f409b5fb8df86936092a0601f4ee4f4ed358` |

Weakening prior equality to revision/digest strings produced exactly three expected failures: changed active, recoverable and legacy-unknown payloads were accepted. Removing the Complete legacy-unknown guard produced exactly one expected legacy-recovery failure, with five passing controls. Both failed at the intended missing-refusal assertion. Root restored the original membership source byte-for-byte (`ca83da58e4ea6d53b4f37a7ec57bac7b6338ab064439707f21d2da4f8fae1e44`) before the final passing build and suite. Logs, TRX, manifests, mutation controls and reviews are retained under the owned `prune-admission-authorization-audit/native-filesystem-gates/` artifact directory.

## Remaining validation boundaries

Candidate construction cannot prove that an Incomplete ledger retained every previous member without the prior Complete ledger; transition code must compare them. Prospective aliases require native collision checks before publication. Known tags and descriptive install/recovery evidence require independent completeness, policy and physical-identity verification. Digest strings are placeholders until canonical calculation and validated serialization; this increment does not directly deserialize these internal constructors with default System.Text.Json. No runtime registration or destructive primitive is introduced. Full enrollment, before-read admission, actual lifetime protection, two-composition proof, safe execution and release/adoption remain open.

## Hosted qualification

[Validate 37925664909](https://github.com/valence-works/nuplane/actions/runs/37925664909) at exact record commit `6f58814de66a59dcedc105fea239b32889d66c6e` passed all six jobs. The full Ubuntu solution passed **1,459**, failed **0**, with fourteen explicit platform/casefold skips. Each Unix platform executed all eleven native cases without skips; both Linux architectures passed the demanded owned ext4 casefold case. Windows passed eleven adapter/parser plus six identity cases without skips. Universal Darwin shim, existing state gates and Linux package-asset verification passed. Root checked exact-head job states and full logs; `hosted-6f58814-all.log` SHA-256 is `12879c7c0e2b34684093c7a8466f1741ae4a5b5b2ea476378ef453c83ace5cad`. This qualifies the record increment, not the remaining runtime protocol.
