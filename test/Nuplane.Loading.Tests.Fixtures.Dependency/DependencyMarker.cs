namespace Plugin.Dependency;

/// <summary>
/// Dependency type used by dependency closure loading tests.
/// </summary>
public class DependencyMarker
{
    /// <summary>
    /// Gets a stable value for reflection-based assertions.
    /// </summary>
    public virtual string Value => "dependency";
}
