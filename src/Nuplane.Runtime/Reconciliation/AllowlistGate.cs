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
        var errors = new List<Exception>();

        foreach (var request in requests)
        {
            if (!trustOptions.IsPackageAllowed(request.Id))
            {
                errors.Add(new InvalidOperationException($"Package '{request.Id}' is not allowlisted."));
                continue;
            }

            accepted.Add(request);
        }

        if (errors.Count > 0)
        {
            throw new AggregateException("One or more package requests are not allowlisted.", errors);
        }
        return accepted;
    }
}
