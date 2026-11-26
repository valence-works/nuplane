# Getting Started

## Primary purpose

This page gets you to your first working dynamic package installation in a .NET application — and then points you to the right next steps.

Install the .NET 10 SDK to build the samples. Nuplane 1.x packages target .NET 10. When upgrading from Nuplane 0.0.15 or earlier, apply the [0.0.16 migration notes](https://github.com/valence-works/nuplane/releases/tag/0.0.16) before starting the upgraded host.

### Install a plugin in the sample host

Run from the repository root:

```bash
dotnet run --project samples/Nuplane.Sample.AspNetCore/Nuplane.Sample.AspNetCore.csproj --no-launch-profile -- --urls http://127.0.0.1:5086
```

From a second terminal at the repository root, pack the plugin and copy it into the sample content
root's watched folder:

```bash
dotnet pack samples/Nuplane.Sample.Plugin/Nuplane.Sample.Plugin.csproj -c Debug
mkdir -p samples/Nuplane.Sample.AspNetCore/packages
cp samples/Nuplane.Sample.Plugin/bin/Debug/Nuplane.Sample.Plugin.1.0.0.nupkg samples/Nuplane.Sample.AspNetCore/packages/
```

Wait a few seconds for reconciliation and check the running host:

```bash
curl -fsS http://127.0.0.1:5086/catalog/packages
curl -fsS http://127.0.0.1:5086/catalog/plugins
```

Look for `Nuplane.Sample.Plugin` and `Nuplane.Sample.Plugin.HelloPlugin` in the responses.

## Recommended reading and usage order

1. [Overview](Overview.md) — understand why Nuplane exists and where its boundaries are.
2. Read this page — learn the minimum mental model and choose a starting scenario.
3. [Usage Guide](Usage-Guide.md) — pick the adoption path that matches your host.
4. Use the sample host and validation quickstart for hands-on confirmation.

## Minimum mental model

Before you open the sample, keep these ideas in mind:

- **Desired state** is the package set Nuplane should make active.
- **Actual state** is the package set currently active in the local store.
- **Reconciliation** compares those sets and applies safe per-package changes.
- **Query-first integration** means your host should read authoritative package or load-state surfaces instead of reconstructing state from observer history.
- **Optional loading** is a module you add only when your host wants Nuplane-managed runtime loading.

## Recommended first-use path

### Path A: Evaluate the core runtime story first

- **Applicability:** `Core`
- Start with the [`Nuplane` setup section](../../README.md).
- Focus on feeds, polling, state persistence, and the idea that the host reacts to package changes.
- Treat query surfaces as the authoritative read path.

### Path B: Validate the sample-backed flow

- **Applicability:** `Core`
- Use [`samples/Nuplane.Sample.AspNetCore/Program.cs`](../../samples/Nuplane.Sample.AspNetCore/Program.cs) and [`samples/Nuplane.Sample.AspNetCore/appsettings.json`](../../samples/Nuplane.Sample.AspNetCore/appsettings.json) to see the recommended sample composition.
- Then use [`specs/014-query-package-catalog/quickstart.md`](../../specs/014-query-package-catalog/quickstart.md) for the maintained end-to-end validation commands.

### Path C: Add runtime loading only if your host needs it

- **Applicability:** `Optional Module`
- Add the loading module when you need package assemblies loaded into the current process.
- Keep in mind that Nuplane still does not define plugin semantics or host activation policy.

## Package versions and feeds

Main-branch preview packages are published to [Feedz](https://f.feedz.io/valence-works/nuplane/nuget/index.json)
as `1.0.0-preview.<workflow-run-number>`. The numeric suffix is the publishing workflow's run number.
Keep references to Nuplane's packages on the same version when upgrading.

Tagged releases use the release tag as their package version, with an optional leading `v` removed,
and publish to both nuget.org and Feedz. Release tag versions are independent of the preview base
version; changing the preview base does not change the version selected by a release tag.

## How the sample teaches the model

The sample host shows the intended split of responsibilities:

- configuration describes Nuplane infrastructure and feed setup;
- the host composes observers and admin/query surfaces in code;
- the sample steers readers toward authoritative catalog reads instead of observer replay;
- the optional loading route is clearly separate from metadata-only package-state access.

## Where to go next

| If you want to... | Continue to | Why |
|-------------------|-------------|-----|
| Choose between core-runtime and loading-enabled usage | [Usage Guide](Usage-Guide.md) | It separates baseline and optional scenarios |
| Understand the sample route composition | [Usage Guide](Usage-Guide.md) | It summarizes configuration-driven and code-driven adoption |
| Validate the end-to-end sample | [`specs/014-query-package-catalog/quickstart.md`](../../specs/014-query-package-catalog/quickstart.md) | It owns the deeper command and evidence flow |
| Learn the vocabulary before continuing | [Concepts and Glossary](Concepts-and-Glossary.md) | It normalizes the terms used across the wiki |

## Repository-owned detail

The following material intentionally stays repository-owned rather than being duplicated here:

- full test and validation command sets;
- exact sample route payload expectations;
- phase-by-phase evolution detail.

See [Source References](_Source-References.md) for the ownership map.

## Canonical repository anchors

- [`README.md`](../../README.md)
- [`samples/Nuplane.Sample.AspNetCore/Program.cs`](../../samples/Nuplane.Sample.AspNetCore/Program.cs)
- [`samples/Nuplane.Sample.AspNetCore/appsettings.json`](../../samples/Nuplane.Sample.AspNetCore/appsettings.json)
- [`specs/014-query-package-catalog/quickstart.md`](../../specs/014-query-package-catalog/quickstart.md)

