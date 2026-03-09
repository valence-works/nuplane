namespace Nuplane.Loading.Tests.Fixtures;

/// <summary>
/// Marker type used by <c>PackageAssemblyLoadContextTests</c> to resolve the fixture
/// assembly path at test time via <c>typeof(FixtureMarker).Assembly.Location</c>.
/// </summary>
public static class FixtureMarker { }

/// <summary>
/// Simple exported type used by scanner resilience tests.
/// </summary>
public sealed class HealthyFixtureType { }
