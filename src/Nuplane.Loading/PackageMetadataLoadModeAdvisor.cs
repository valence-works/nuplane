using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nuplane.Loading;

internal sealed class PackageMetadataLoadModeAdvisor : IPackageLoadModeAdvisor
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

            var result = _reader.Read(package.Id, package.Version, package.InstallPath);
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
}
