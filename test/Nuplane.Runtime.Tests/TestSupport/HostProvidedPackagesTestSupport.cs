using Nuplane.Reconciliation.Configuration;

namespace Nuplane.Runtime.Tests.TestSupport;

internal static class HostProvidedPackagesTestSupport
{
    /// <summary>
    /// Builds <see cref="HostProvidedPackagesOptions"/> with exactly <paramref name="entries"/>,
    /// replacing the default entries rather than appending to them. Shared by the validator, the
    /// configuration reader, and the resolver tests so the same arrange step reads the same way
    /// everywhere it is used.
    /// </summary>
    public static HostProvidedPackagesOptions WithEntries(params string[] entries)
    {
        var options = new HostProvidedPackagesOptions();
        options.Entries.Clear();
        foreach (var entry in entries)
        {
            options.Entries.Add(entry);
        }

        return options;
    }
}
