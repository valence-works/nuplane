namespace Nuplane.Restore;

/// <summary>
/// Turns the three configured store paths into absolute ones, using the overrides and the
/// <see cref="NuplaneRestoreOptions.BasePath"/> a host-free caller supplies instead of the
/// calling process's own directories.
/// </summary>
/// <remarks>
/// A running host resolves <c>FeedResolution:PackageInstallRoot</c> and the state file against
/// <see cref="AppContext.BaseDirectory"/>, and relative values against the current directory. For a
/// restoring tool both of those name the tool, not the host, so every unpinned path is refused
/// rather than defaulted: a restore that quietly populates the wrong directory reports success and
/// leaves the host empty.
/// </remarks>
internal sealed class RestorePathResolver
{
    private const string DefaultInstallRootRelativePath = ".nuplane/packages";
    private const string DefaultStateFileRelativePath = ".nuplane/store-state.json";

    private readonly NuplaneRestoreOptions _options;
    private readonly string? _basePath;

    public RestorePathResolver(NuplaneRestoreOptions options)
    {
        _options = options;
        _basePath = string.IsNullOrWhiteSpace(options.BasePath)
            ? null
            : RequireAbsolute(options.BasePath, nameof(NuplaneRestoreOptions.BasePath));
    }

    /// <summary>
    /// The already-validated, absolute form of <see cref="NuplaneRestoreOptions.BasePath"/>, or
    /// <see langword="null"/> when it was not set — the same value every other path on this resolver
    /// anchors to, exposed so <see cref="RestoreComposition"/> can hand it to
    /// <see cref="Builder.NuplaneBuilder.BasePath"/> for module-owned builder extensions to read.
    /// </summary>
    public string? BasePath => _basePath;

    public string ResolveInstallRoot(string? configuredValue) =>
        Resolve(
            _options.InstallRoot,
            nameof(NuplaneRestoreOptions.InstallRoot),
            configuredValue,
            "Nuplane:FeedResolution:PackageInstallRoot",
            DefaultInstallRootRelativePath);

    public string ResolveStateFilePath(string? configuredValue) =>
        Resolve(
            _options.StateFilePath,
            nameof(NuplaneRestoreOptions.StateFilePath),
            configuredValue,
            "Nuplane:StoreRegistry:StateFilePath",
            DefaultStateFileRelativePath);

    /// <summary>
    /// Resolves the package lock file. Unlike the install root and the state file this never
    /// refuses, because its configured default — the bare relative name <c>nuplane.lock.json</c> —
    /// is a value the operator never typed. A relative value anchors to
    /// <see cref="NuplaneRestoreOptions.BasePath"/> when there is one and otherwise to the state
    /// file's own directory, so it stays inside the store being restored and is never read from the
    /// restoring process's current directory.
    /// </summary>
    public string ResolveLockFilePath(string configuredValue, string resolvedStateFilePath)
    {
        if (!string.IsNullOrWhiteSpace(_options.LockFilePath))
        {
            return RequireAbsolute(_options.LockFilePath, nameof(NuplaneRestoreOptions.LockFilePath));
        }

        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            // Blank is invalid; leave it so the lock file options validator says so.
            return configuredValue;
        }

        if (Path.IsPathRooted(configuredValue))
        {
            return Path.GetFullPath(configuredValue);
        }

        return Path.GetFullPath(Path.Combine(
            _basePath ?? Path.GetDirectoryName(resolvedStateFilePath)!,
            configuredValue));
    }

    private string Resolve(
        string? overrideValue,
        string overrideOptionName,
        string? configuredValue,
        string configurationKey,
        string defaultRelativePath)
    {
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            return RequireAbsolute(overrideValue, overrideOptionName);
        }

        if (!string.IsNullOrWhiteSpace(configuredValue))
        {
            if (Path.IsPathRooted(configuredValue))
            {
                return Path.GetFullPath(configuredValue);
            }

            return _basePath is not null
                ? Path.GetFullPath(Path.Combine(_basePath, configuredValue))
                : throw new InvalidOperationException(
                    $"'{configurationKey}' is the relative path '{configuredValue}', which a running host resolves against its own current directory. "
                    + $"Set {nameof(NuplaneRestoreOptions)}.{nameof(NuplaneRestoreOptions.BasePath)} to the host's base directory, or "
                    + $"{nameof(NuplaneRestoreOptions)}.{overrideOptionName} to an absolute path, so the restore cannot write somewhere else.");
        }

        return _basePath is not null
            ? Path.GetFullPath(Path.Combine(_basePath, defaultRelativePath))
            : throw new InvalidOperationException(
                $"'{configurationKey}' is not configured, and a running host would default it to '{defaultRelativePath}' under its own base directory — which for this process is the restoring tool's. "
                + $"Set {nameof(NuplaneRestoreOptions)}.{nameof(NuplaneRestoreOptions.BasePath)} to the host's base directory, or "
                + $"{nameof(NuplaneRestoreOptions)}.{overrideOptionName} to an absolute path.");
    }

    private static string RequireAbsolute(string value, string optionName) =>
        Path.IsPathRooted(value)
            ? Path.GetFullPath(value)
            : throw new ArgumentException(
                $"{nameof(NuplaneRestoreOptions)}.{optionName} must be an absolute path, but was '{value}'.",
                nameof(NuplaneRestoreOptions));
}
