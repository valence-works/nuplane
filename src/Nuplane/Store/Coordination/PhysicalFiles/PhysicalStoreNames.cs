namespace Nuplane.Store.Coordination.PhysicalFiles;

/// <summary>Validates portable single-component names for handle-relative filesystem operations.</summary>
internal static class PhysicalStoreNames
{
    /// <summary>Validates a single component without normalizing or interpreting its Unicode spelling.</summary>
    /// <param name="name">The exact component supplied to a handle-relative operation.</param>
    /// <exception cref="ArgumentException">The value is blank, a dot component, or contains a separator, colon, or NUL.</exception>
    internal static void ValidateSingleComponent(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name is "." or "..")
            throw new ArgumentException("A physical store name must be one ordinary component.", nameof(name));

        if (name.IndexOf('/') >= 0 ||
            name.IndexOf('\\') >= 0 ||
            name.IndexOf(':') >= 0 ||
            name.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("A physical store name cannot contain a separator, colon, or NUL.", nameof(name));
        }
    }
}
