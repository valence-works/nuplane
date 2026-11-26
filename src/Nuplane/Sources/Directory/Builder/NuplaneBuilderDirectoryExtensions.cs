using Nuplane.Builder;
using Nuplane.Sources.Directory.Registration;

namespace Nuplane.Sources.Directory.Builder;

/// <summary>
/// Builder extensions for configuring directory-backed Nuplane feeds
/// from the <see cref="NuplaneBuilder"/> fluent API.
/// </summary>
public static class NuplaneBuilderDirectoryExtensions
{
    /// <summary>
    /// Registers a directory-backed feed.
    /// </summary>
    /// <param name="builder">The Nuplane builder to extend.</param>
    /// <param name="name">The unique name of the feed.</param>
    /// <param name="path">The directory path containing <c>.nupkg</c> files.</param>
    /// <param name="configure">An optional callback to configure directory feed options.</param>
    /// <returns>The same <see cref="NuplaneBuilder"/> for chaining.</returns>
    /// <remarks>
    /// A relative <paramref name="path"/> resolves against <see cref="NuplaneBuilder.BasePath"/> when
    /// the builder has one, and against the current directory otherwise.
    /// </remarks>
    public static NuplaneBuilder AddDirectoryFeed(
        this NuplaneBuilder builder,
        string name,
        string path,
        Action<NuplaneDirectoryFeedConfiguration>? configure = null) =>
        AddDirectoryFeed(builder, name, path, configure, configuredBasePath: null);

    internal static NuplaneBuilder AddDirectoryFeed(
        this NuplaneBuilder builder,
        string name,
        string path,
        Action<NuplaneDirectoryFeedConfiguration>? configure,
        Func<string?>? configuredBasePath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var config = new NuplaneDirectoryFeedConfiguration();
        configure?.Invoke(config);

        var dirOptions = new NuplaneDirectoryFeedOptions
        {
            DirectoryPath = path,
            Trust = config.Trust,
            TrustConfigurationPath = config.TrustConfigurationPath,
            Role = config.Role,
            Watch = config.Watch,
            DebounceWindow = config.DebounceWindow,
        };

        DirectorySourceRegistrationServices.RegisterFeed(
            builder.Services,
            name,
            dirOptions,
            config.IncludePatterns,
            config.Credentials,
            builder.BasePath,
            configuredBasePath);

        DirectorySourceRegistrationServices.AddRegistrationMarker(
            builder.Services,
            name,
            config.IncludePatterns,
            config.HasExplicitUnrestrictedPackageSelection);

        return builder;
    }
}
