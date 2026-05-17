# Package Authoring

## Purpose

This page gives package authors the Nuplane-specific metadata shape for packages that need predictable runtime loading behavior.

## Load-mode metadata

Packages that require framework/default assembly load context integration can declare that requirement once in package-root `nuplane.json`:

```json
{
  "schemaVersion": 1,
  "loading": {
    "loadMode": "HostIntegrated",
    "scope": "DependencyClosure",
    "reason": "Uses framework type resolution and runtime scheduler integration."
  }
}
```

Place the file at the root of the NuGet package next to the package contents. Nuplane v1 loading metadata is not discovered from `build/`, `contentFiles/`, or nuspec metadata.

Allowed values:

- `schemaVersion`: `1`
- `loading.loadMode`: `HostIntegrated` or `Collectible`
- `loading.scope`: `DependencyClosure` or `PackageOnly`
- `loading.reason`: optional bounded human-readable explanation

`HostIntegrated` is a requirement. With `DependencyClosure`, Nuplane promotes the loadable graph containing the declaring package so framework services, by-name assembly resolution, provider metadata, schedulers, migrations, and similar long-lived integrations can see the package graph consistently.

`Collectible` is only a preference. It does not force an application down from a `HostIntegrated` default or another package's host-integrated requirement.

## Application overrides

Application authors remain in control. `Loading:PackageLoadModes` overrides win over metadata for the same package, and Nuplane records a suppression diagnostic so operators can see that package metadata was ignored intentionally.

Use metadata for package-owned requirements that every host would otherwise need to rediscover. Use app overrides for deployment-specific policy, temporary migration, or emergency compatibility controls.

## Trust model

Nuplane reads `nuplane.json` only after the package has already been resolved and installed through the configured package source, trust, and integrity paths. Metadata is trusted only as much as the package itself.

Metadata cannot:

- grant additional package source trust;
- bypass package validation or lock-file policy;
- mutate package/store state during selection;
- define host activation semantics;
- sandbox untrusted package code.

Invalid, unsupported, unreadable, or oversized metadata is ignored for selection and reported through load-mode diagnostics.

## Related pages

- [Usage Guide](Usage-Guide.md)
- [Concepts and Glossary](Concepts-and-Glossary.md)
