# Canonical digest evidence

T032 adds the internal `ProtectionDigest.StateBody`, `Protection` and `Ledger` entry points. [The data model](../data-model.md#canonical-encoding-revision-1) defines revision-1 framing, every nested field tag, nullable values, case rules, exact native strings and sequence/set comparators. The body shares the serializer's existing optional-map normalization. Protection field encoding is shared between standalone and nested values; a layer omits only its own digest.

These are integrity calculations over descriptive records. They perform no filesystem access and confer no membership, graph completeness, admission, publication or deletion authority. T031 remains partial until actual saved/reopened content is verified by coordinated publication. T033/T034 and the full shared-root pruning proof remain open.

## Review and verification

Root reviewed production encoding, the normative tag table, all tests and the opt-in framework workflow. Independent review found no remaining blocker; root checked all six final source/document/project/workflow hashes against its review. The review artifact SHA-256 is `1908d33cff29124fcc875a5e2097be93eba7fc2cd76661ce8bda7cf5bd7e09b7`. Final production SHA-256 is `051981a01698fe307f222ab1ed38181c5288427161d254c8a098854337b3e4d5`; final test SHA-256 is `7feff9b8d1a5ab651204fa17422d2339f48e37a02d964ba84d577fa14824fc74`.

The final core build passed net8/net9/net10 with zero warnings/errors. The full local Store suite passed **254**, failed **0**, with **14** explicit Windows/casefold skips. All **28 new digest cases** executed. The focused serialization/digest suite separately passed **57**, failed **0**, skipped **0** on each of net8/net9/net10. Those local runs use macOS ARM64; hosted platform qualification for this increment is pending.

Coverage binds every persisted body/protection/ledger field, all member-binding variants, complete pending/retirement evidence, nested digests, exact paths/identities, enum name/value assignments, nullable versus empty values, duplicate request/edge multiplicity, sequence versus set order, dictionary collisions, strict UTF-8, culture independence and save/reload normalization. Independent simple golden vectors and a fixed Unicode preimage pin framing/case behavior across the three runtimes. A populated graph and pending-ledger independent golden vector remain useful hardening under T044 before stable encoding release; field-mutation and nested framing tests already execute here.

Root corrected the initial independent KnownEmpty reference vector: nullable `Graphs` must retain its presence byte. The original reference is preserved; version 2 uses the normative encoding and matches production. Corrected reference SHA-256: `6e9fea3d66813f2837c1180f07a07dfe806ed3229c5091180cd6f7d2da7d37bb`. Unicode preimage artifact SHA-256: `901bda6ff4eba4646d50667a50cd032c169f7b846dd664a7aadaffafba0f0bda`; its independently reassembled digest is `001b1071d77410bfca65429b3b1789d0ec8f77ccbd0623545af5f20da9e644a1`.

Three causal controls independently removed timestamp binding, recovery-policy binding and nested protection-digest binding. Each caused exactly **one intended NotEqual failure**, zero passes/skips in its selected test. Production was restored byte-for-byte after every control before final passing gates. The earlier failed command-line TFM override is preserved; the corrected opt-in `NuplaneProtectionFrameworkMatrix=true` project configuration restores all target assets while keeping ordinary Store runs net10-only.

| Final evidence | SHA-256 |
|---|---|
| Core all-TFM build log | `45109f503119244a6ff0a3c8d551337351a9c7688092e00fccf9e7c44a2a037b` |
| Full Store suite log | `0b8dcc2b0af1b057de7bcee6d75e7b4ef8200955591c7778ccc025329ed0fe47` |
| Focused net8 log | `c072200b607d47aa8ccb8be0b5b0ef41bfdd055abdca66e7da9dfb6e75826f7e` |
| Focused net9 log | `18312c3bec6b3527fd3b64f56a899d8e3dcc40d1cf6eb55de533f64c1c7a7e83` |
| Focused net10 log | `e65e019d6763804bb43cf39c3a61f6204624e7d19da0bff51fe7024d76b79711` |
| Matching 755-input manifest for all final gates | `140001cec1fcab7d37cb5fe889992b2d770687d9bbfeae589e0ff09ce7d63cb9` |
| Timestamp causal-control log | `f4953d95d7377a58f7ef44577dced2b221b9d2fea43e9c363d36de1076b139e9` |
| Recovery-policy causal-control log | `49740b4f9c7bf8b71e6886c623a45866b13736bf27b000acb7cebe803899d53a` |
| Nested-digest causal-control log | `5ec2ce04adb1632b58effe8ee8e63a3b71ebb2842fb6618021a2ae8b24db371e` |

Logs, TRX, source manifests, reference programs/preimages, original mutation backup and independent review are retained in the owned `prune-admission-authorization-audit/native-filesystem-gates/` artifact directory. [Serialization qualification](protection-serialization.md#hosted-qualification) belongs to its earlier exact commit and is not reused as hosted digest proof.
