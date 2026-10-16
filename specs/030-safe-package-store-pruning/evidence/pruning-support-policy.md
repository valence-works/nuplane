# Pruning support policy

On 2026-10-11 the maintainer answered the compatibility decision card:

> Keep ordinary Nuplane compatibility; restrict pruning to verified environments and participating clients.

The other option was to extend coordinated writes to every currently supported ordinary Nuplane environment before shipping pruning. The maintainer selected the narrower optional pruning contract. [Issue108 records the decision and delivery transition](https://github.com/valence-works/nuplane/issues/108#issuecomment-6112951062).

## Required behavior

- Preserve ordinary Nuplane's existing platform and filesystem compatibility for stores outside the coordinated pruning contract.
- Permit enrollment and pruning only after native filesystem safeguards are verified and the operator establishes that every client accessing that physical store participates in coordination. This includes clients without local maintenance enabled.
- Require a fully quiescent cutover for existing stores. Legacy clients and arbitrary external readers/writers cannot be detected automatically and are outside the enrolled-store support contract.
- Refuse unsafe or uncertain access within that contract. An unsupported native probe is not evidence of an unenrolled root or state slot; a participating writer must never silently use an unguarded fallback.
- Preserve existing required interfaces and ordinary serializer normalization. Define and verify the participation/routing boundary before enabling public enrollment or execution.

## Delivery state

This decision resolves the owner-answer hold on public writer-routing design and support policy. It is not evidence that those paths are implemented, that schema-2 activation is safe, or that physical deletion and recovery are accepted. The implementation checklist retains those gates. No production behavior changes in this documentation increment.

The original upstream extraction, Nuplane0.0.11 and CShells0.0.30 releases (including CShells.Nuplane), and Elsa Foundation adoption have shipped independently of pruning. This policy applies to the remaining optional pruning work.
