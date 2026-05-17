namespace Nuplane.Loading;

internal sealed class PackageMetadataLoadModeAdvisor(PackageMetadataLoadModeReader reader) : IPackageLoadModeAdvisor
{
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

            var result = reader.Read(package.Id, package.Version, package.InstallPath);
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
