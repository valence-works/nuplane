# Observer interfaces for Nuplane 1.0

Retain the four observer interfaces for the 1.0 API. An ordinary host can implement one callback on `INuplaneObserver` and register it with `OnPackagesChanged<T>()`. The coordinated store needs explicit participation and gives observers separate callbacks for two stages with different lock ownership. [Review issue #141](https://github.com/valence-works/nuplane/issues/141) · [Default callbacks #126](https://github.com/valence-works/nuplane/issues/126)

| Interface | Contract |
| --- | --- |
| `INuplaneObserver` | Ordinary reconciliation callbacks. Each callback defaults to a no-op. |
| `IPackagePathIndependentNuplaneObserver` | An explicit promise that callbacks never open or retain package paths. Coordinated dispatch can call the ordinary callbacks for this observer. |
| `IScopedNuplaneObserver` | Coordinated callbacks with a borrow owned by the awaited dispatcher. Each scoped overload defaults to a no-op. Retained reads require a graph-use lease acquired while the borrow is live. |
| `IPostLoadingNuplaneObserver` | A scoped observer that also receives an awaited callback after coordinated loading and failure publication, with root admission and the store lock released. Package-store reads in that callback require fresh admission. Implementing the completion callback is part of opting into this stage. |

A single interface would still need explicit opt-ins for path independence, scoped access, and deferred completion. The dispatcher would need to validate those declarations before doing any work. A base and scoped pair would require a logging-only observer to implement scoped overloads or declare its path independence some other way. The existing interfaces make the guarantees discoverable in the type system and let ordinary hosts start from the same small base contract. [Coordinated dispatch](../../src/Nuplane/Events/ObserverEventDispatcher.Scoped.cs) · [Post-loading dispatch](../../src/Nuplane/Events/ObserverEventDispatcher.PostLoading.cs)

## Consumer impact

- **CShells.Nuplane** keeps both completion callbacks during and after its move. `NuplaneRefreshCoordinator.OnPackagesReconciledAsync` refreshes ordinary compositions. `OnPackageLoadingCompletedAsync` refreshes coordinated compositions after loading and lock release. The three ordinary and four scoped empty stubs can be deleted once the adapter targets the new Nuplane package. Retain the collectible-generation regression from the integration to cover the placement of the post-loading refresh. [CShells #215](https://github.com/valence-works/cshells/pull/215) · [CShells move #178](https://github.com/valence-works/cshells/issues/178)
- **Elsa Modular Server** can keep `MyPackageObserver` as written on ordinary stores. Its changing, changed, and failure callbacks all log events. If it participates in coordinated stores, it can declare `IPackagePathIndependentNuplaneObserver` after verifying that the callbacks and their consumers only log metadata. Its existing callback bodies work with that marker. [Elsa MyPackageObserver](https://github.com/elsa-workflows/elsa-core/blob/main/core/src/apps/Elsa.ModularServer.Web/MyPackageObserver.cs)

The API inventory for #119 should list these four contracts and the builder registration. Revisit the hierarchy if a consumer needs a combination of post-loading and scoped behavior that the current types cannot represent. [Public API freeze #119](https://github.com/valence-works/nuplane/issues/119)
