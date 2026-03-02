using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;

namespace Nuplane.Runtime.Reconciliation;

public sealed class AllowlistGate
{
    public IReadOnlyList<PackageRequest> Enforce(IReadOnlyList<PackageRequest> requests, SourceTrustOptions trustOptions)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(trustOptions);

        var accepted = new List<PackageRequest>(requests.Count);
        foreach (var request in requests)
        {
            if (!trustOptions.IsPackageAllowed(request.Id))
            {
                throw new InvalidOperationException($"Package '{request.Id}' is not allowlisted.");
            }

            accepted.Add(request);
        }

        return accepted;
    }
}
