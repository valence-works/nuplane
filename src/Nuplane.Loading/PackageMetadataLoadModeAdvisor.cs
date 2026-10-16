using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;

namespace Nuplane.Loading;

internal sealed class PackageMetadataLoadModeAdvisor : IScopedPackageLoadModeAdvisor
{
    private readonly PackageMetadataLoadModeReader _reader;
    private readonly ILogger<PackageMetadataLoadModeAdvisor> _logger;

    public PackageMetadataLoadModeAdvisor(
        PackageMetadataLoadModeReader reader,
        ILogger<PackageMetadataLoadModeAdvisor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(reader);

        _reader = reader;
        _logger = logger ?? NullLogger<PackageMetadataLoadModeAdvisor>.Instance;
    }

    public string Name => "package-metadata";

    public ValueTask<IReadOnlyList<LoadModeAdvisorResult>> EvaluateAsync(
        LoadModeAdvisorContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var results = new List<LoadModeAdvisorResult>();
        foreach (var package in context.Packages
            .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = context.GraphUseLeases.Count == 0
                ? _reader.Read(package.Id, package.Version, package.InstallPath)
                : ReadWithLease(context.GraphUseLeases, package);
            if (!result.MetadataFound)
            {
                continue;
            }

            if (!result.IsValid || result.Metadata?.Loading is null)
            {
                results.Add(new(
                    Name,
                    package.Id,
                    package.Version,
                    PackageLoadMode.Collectible,
                    LoadModeScopes.PackageOnly,
                    LoadModeReasonCodes.MetadataInvalid,
                    Reason: null,
                    IsValid: false,
                    result.Diagnostic ?? "Package metadata is invalid."));
                continue;
            }

            _logger.PackageLoadMetadataDiscovered(
                package.Id,
                package.Version,
                context.GraphKey,
                result.Metadata.Loading.LoadMode,
                result.Metadata.Loading.Scope);
            results.Add(new(
                Name,
                package.Id,
                package.Version,
                result.Metadata.Loading.LoadMode,
                result.Metadata.Loading.Scope,
                LoadModeReasonCodes.PackageMetadata,
                result.Metadata.Loading.Reason));
        }

        return ValueTask.FromResult<IReadOnlyList<LoadModeAdvisorResult>>(results);
    }

    private PackageMetadataLoadModeReadResult ReadWithLease(
        IReadOnlyList<PackageGraphUseLease> leases,
        ResolvedPackage package)
    {
        var matches = leases.Where(lease => lease.Snapshot.Nodes.Any(node =>
                string.Equals(node.Install.PackageId, package.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(node.Install.Version, package.Version, StringComparison.Ordinal)))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                "The package metadata read does not have exactly one matching published graph-use lease.");
        }

        return _reader.Read(package.Id, package.Version, package.InstallPath, matches[0]);
    }
}
