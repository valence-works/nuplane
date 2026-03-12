using Nuplane.Abstractions;
using Nuplane.Trust.Source;

namespace Nuplane.Trust;

/// <summary>
/// Enforces package allowlist rules by validating requests against the allowed package
/// identifiers and ensuring install paths are within the trusted store root.
/// </summary>
public sealed class AllowlistGate : IAllowlistGate
{
    /// <inheritdoc />
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

    /// <inheritdoc />
    public void EnsureActiveStorePath(string packageId, string activeInstallPath, string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeInstallPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var normalizedRoot = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(activeInstallPath);

        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Package '{packageId}' active install path '{activeInstallPath}' is outside trusted store root '{rootDirectory}'.");
        }
    }
}

