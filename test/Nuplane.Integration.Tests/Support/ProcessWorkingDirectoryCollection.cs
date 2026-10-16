namespace Nuplane.Integration.Tests.Support;

// Changing the process working directory affects every test collection, including ASP.NET host setup.
[CollectionDefinition(nameof(ProcessWorkingDirectoryCollection), DisableParallelization = true)]
public sealed class ProcessWorkingDirectoryCollection
{
}
