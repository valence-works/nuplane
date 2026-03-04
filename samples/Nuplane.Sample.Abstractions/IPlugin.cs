namespace Nuplane.Sample.Abstractions;

/// <summary>
/// Defines a minimal plugin contract used by the Nuplane samples.
/// </summary>
public interface IPlugin
{
    /// <summary>
    /// Gets the display name of the plugin implementation.
    /// </summary>
    string Name { get; }
}
