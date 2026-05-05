using Plugin.Dependency;

namespace Plugin.Root;

/// <summary>
/// Root type whose metadata requires the dependency fixture assembly.
/// </summary>
public sealed class RootMarker : DependencyMarker
{
    /// <inheritdoc />
    public override string Value => "root:" + base.Value;
}
