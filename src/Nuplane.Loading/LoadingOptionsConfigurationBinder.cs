using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Nuplane.Loading;

/// <summary>
/// Binds a <c>Nuplane:Loading</c> configuration section into <see cref="LoadingOptions"/>, refusing rather than
/// dropping a <see cref="LoadingOptions.SharedAssemblies"/> entry that does not bind.
/// </summary>
/// <remarks>
/// The configuration binder skips, without reporting it, a collection element it cannot construct — an entry
/// with no <c>Name</c>, no <c>MajorVersion</c>, or a <c>MajorVersion</c> that is not a number. A host would
/// then run without the shared identity it configured, and each package would bind a private copy of the
/// assembly instead of the host's. So the entries bound are counted against the entries configured, and every
/// entry that did not bind is recorded in <see cref="LoadingOptions.ConfigurationErrors"/> under its
/// configuration path, for <see cref="LoadingOptionsValidator"/> to fail on.
/// </remarks>
internal static class LoadingOptionsConfigurationBinder
{
    /// <summary>
    /// Binds <paramref name="loadingSection"/> into <paramref name="options"/> and records a configuration error
    /// for each <c>SharedAssemblies</c> entry the binder dropped.
    /// </summary>
    internal static void Bind(LoadingOptions options, IConfigurationSection loadingSection)
    {
        var boundBefore = options.SharedAssemblies.Count;
        loadingSection.Bind(options);

        var sharedAssembliesSection = loadingSection.GetSection(nameof(LoadingOptions.SharedAssemblies));
        var configured = sharedAssembliesSection.GetChildren().ToArray();
        var bound = options.SharedAssemblies.Count - boundBefore;
        if (IsSingleObject(configured))
        {
            options.ConfigurationErrors.Add(
                $"'{sharedAssembliesSection.Path}' must be an array of entries; found an object with keys {string.Join(", ", configured.Select(static child => child.Key))}. " +
                "Configure it as a list, for example \"SharedAssemblies\": [ { \"Name\": \"...\", \"MajorVersion\": 1 } ].");
            return;
        }

        if (bound == configured.Length)
        {
            return;
        }

        var refusals = configured.Select(DescribeUnboundEntry).OfType<string>().ToArray();
        options.ConfigurationErrors.AddRange(refusals.Length > 0
            ? refusals
            : [$"'{sharedAssembliesSection.Path}' configures {configured.Length} shared assembly entries, but only {bound} bound."]);
    }

    // An array's children are keyed 0, 1, 2...; children keyed by anything else mean an object was configured.
    private static bool IsSingleObject(IConfigurationSection[] configured) =>
        configured.Length > 0 && configured.Any(static child => !int.TryParse(child.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _));

    private static string? DescribeUnboundEntry(IConfigurationSection entry)
    {
        try
        {
            return entry.Get<SharedAssemblyIdentity>() is null
                ? Refusal(entry, "it has no Name, PublicKeyToken or MajorVersion values.")
                : null;
        }
        catch (InvalidOperationException ex)
        {
            return Refusal(entry, ex.Message);
        }
    }

    private static string Refusal(IConfigurationSection entry, string reason) =>
        $"Shared assembly entry '{entry.Path}' could not be bound, and is refused rather than ignored: {reason} " +
        "An entry needs a Name and a MajorVersion; its PublicKeyToken is optional, and null, empty or omitted means unsigned.";
}
