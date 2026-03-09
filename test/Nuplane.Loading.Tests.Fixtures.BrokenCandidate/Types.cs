using Nuplane.Loading.Tests.Fixtures.BrokenDependency;

namespace Nuplane.Loading.Tests.Fixtures.BrokenCandidate;

public static class BrokenCandidateMarker
{
}

public sealed class HealthyCandidateType
{
}

public sealed class BrokenCandidateType : MissingBase
{
}
