namespace Nuplane.Loading.Tests.Fixtures;

/// <summary>
/// Root fixture type used by graph loading tests.
/// </summary>
public sealed class GraphRootFixture
{
    /// <summary>
    /// Gets a value from the dependency fixture to force dependency assembly binding.
    /// </summary>
    public string GetDependencyValue() => new GraphDependencyFixture().Value;
}

/// <summary>
/// Dependency fixture type used by graph loading tests.
/// </summary>
public sealed class GraphDependencyFixture
{
    /// <summary>
    /// Gets a stable dependency value for graph loading assertions.
    /// </summary>
    public string Value => "dependency";
}
