namespace Nuplane.Loading.Tests;

internal static class TestFixtureAssemblyPaths
{
    public static string FindProjectAssembly(string projectDirectoryName, string assemblyFileName)
    {
        var searchedPaths = new List<string>();
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        var configurations = GetCandidateConfigurations();
        while (current is not null)
        {
            foreach (var configuration in configurations)
            {
                var candidate = Path.Combine(current.FullName, "test", projectDirectoryName, "bin", configuration, "net10.0", assemblyFileName);
                searchedPaths.Add(candidate);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Fixture assembly '{assemblyFileName}' was not found. Searched: {string.Join(", ", searchedPaths)}", assemblyFileName);
    }

    private static IReadOnlyList<string> GetCandidateConfigurations()
    {
        var currentConfiguration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name;
        return new[] { currentConfiguration, "Debug", "Release" }
            .Where(static configuration => !string.IsNullOrWhiteSpace(configuration))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }
}
