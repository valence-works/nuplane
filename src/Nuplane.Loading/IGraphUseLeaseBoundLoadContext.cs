using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Loading;

/// <summary>Exposes the existing context-owned lease for deferred package assembly reads.</summary>
internal interface IGraphUseLeaseBoundLoadContext
{
    PackageGraphUseLease? GraphUseLease { get; }
}
