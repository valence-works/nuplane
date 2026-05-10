using System.Reflection;

namespace Nuplane.Loading.Tests;

public sealed class HostIntegratedAssemblyResolverDiagnosticsTests
{
    [Fact]
    public void TryResolve_WhenAssemblyIsNotActive_ReturnsNotFoundDiagnostic()
    {
        var sut = new HostIntegratedAssemblyResolutionCatalog();

        var resolved = sut.TryResolve(new AssemblyName("Missing.Framework.Extension"), out var assembly, out var diagnostic);

        Assert.False(resolved);
        Assert.Null(assembly);
        Assert.Equal("not-found", diagnostic.Outcome);
        Assert.Contains("Missing.Framework.Extension", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }
}
