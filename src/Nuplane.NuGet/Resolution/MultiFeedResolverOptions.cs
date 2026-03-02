namespace Nuplane.NuGet.Resolution;

public sealed class MultiFeedResolverOptions
{
    public List<string> OrderedFeeds { get; } = [];

    public HashSet<string> UnavailableFeeds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool StopOnFirstUnavailable { get; set; }
}