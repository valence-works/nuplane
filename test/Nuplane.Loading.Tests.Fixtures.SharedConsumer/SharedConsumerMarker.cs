using Nuplane.Loading.Tests.Fixtures;

namespace Plugin.SharedConsumer;

/// <summary>
/// Exposes a type from the fixture assembly as this assembly binds it, so a test can tell whether a package that
/// carries its own copy of that assembly was bound to the host's copy or to its own.
/// </summary>
public static class SharedConsumerMarker
{
    /// <summary>
    /// Gets <see cref="HealthyFixtureType"/> as resolved from this assembly's load context.
    /// </summary>
    public static Type SharedType => typeof(HealthyFixtureType);
}
