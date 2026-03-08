# Contract: Feed Version Enumerator

## Interface
```csharp
internal interface IFeedVersionEnumerator
{
    Task<PackageVersionList> EnumerateVersionsAsync(
        FeedDefinition feed,
        string packageId,
        CancellationToken cancellationToken);
}
```

**Location**: Interface in `Nuplane.Runtime/Feeds/Versioning/`  
**Implementation**: `NuGetFeedVersionEnumerator` in `Nuplane.NuGet/`

## Implementation Notes

`NuGetFeedVersionEnumerator` uses `NuGet.Protocol`:
```csharp
var source = new PackageSource(feed.ServiceIndex);
var repository = Repository.Factory.GetCoreV3(source);
var resource = await repository.GetResourceAsync<FindPackageByIdResource>(ct);
var versions = await resource.GetAllVersionsAsync(packageId, cacheContext, NullLogger.Instance, ct);
```

Returned `NuGetVersion` objects are converted to sorted version strings for the `PackageVersionList`.

## Behavioral Contract
- Enumeration MUST query the NuGet V3 feed using `NuGet.Protocol`'s `FindPackageByIdResource`.
- The `SourceRepository` MUST be constructed from the feed's `ServiceIndex` URL.
- Returned versions MUST be converted to strings and sorted ascending by SemVer.
- Enumeration MUST be deterministic: the same feed contents MUST produce the same ordered version list.
- The `PackageVersionList.Versions` contains version strings (not typed objects), keeping the interface NuGet-agnostic.

## Caching Behavioral Contract
- `CachedFeedVersionEnumerator` MUST wrap `IFeedVersionEnumerator` as a decorator.
- Cache key: `{feedName}:{lowercasePackageId}`.
- Cache entries MUST expire after `FeedResolutionOptions.VersionCacheTtl`.
- A `VersionCacheTtl` of `TimeSpan.Zero` MUST disable caching (every call passes through).
- Cache MUST be thread-safe (`ConcurrentDictionary`).
- `PackageVersionList.EnumeratedAt` MUST reflect the timestamp of the original enumeration, not cache retrieval time.

## Error Contract
- Package not found on feed: MUST return an empty `PackageVersionList` (not an error). `FindPackageByIdResource` returns an empty enumerable in this case.
- Feed error (HTTP 4xx/5xx): propagated as `FatalProtocolException` for the caller to handle.
- Network failure: propagated as an appropriate exception; cache decorator MUST NOT return stale data after TTL expiry on failure.
- Authentication failure: propagated as exception via NuGet.Protocol's credential handling.

## Test Contract
- Must verify version list is SemVer-sorted ascending.
- Must verify empty feed returns empty list (not exception).
- Must verify cache hit returns previous result within TTL.
- Must verify cache miss after TTL triggers fresh enumeration.
- Must verify `TimeSpan.Zero` TTL disables caching.
- Must verify feed errors propagate as exceptions.
