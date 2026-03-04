using Nuplane.Sample.Abstractions;

namespace Nuplane.Sample.Plugin;

/// <summary>
/// A minimal sample plugin implementation for end-to-end discovery tests.
/// </summary>
public sealed class HelloPlugin : IPlugin
{
    /// <inheritdoc />
    public string Name => "Hello";
}
