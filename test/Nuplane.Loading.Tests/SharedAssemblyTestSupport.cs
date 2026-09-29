using System.Reflection;
using System.Runtime.Loader;
using Nuplane.Loading.Tests.Fixtures;

namespace Nuplane.Loading.Tests;

/// <summary>
/// Support for the shared-assembly tests.
/// <para>
/// This test project references <c>Nuplane.Loading.Tests.Fixtures</c>, an unsigned assembly, so the default load
/// context holds the host's copy of it. The <c>Plugin.SharedConsumer</c> package carries its own copy of that
/// assembly beside an assembly that binds one of its types, which is what lets a test tell which copy a package
/// was bound to.
/// </para>
/// </summary>
internal static class SharedAssemblyTestSupport
{
    public const string ConsumerPackageId = "Plugin.SharedConsumer";

    private const string ConsumerProjectDirectoryName = "Nuplane.Loading.Tests.Fixtures.SharedConsumer";

    /// <summary>Gets the host's copy of the assembly the tests declare shared.</summary>
    public static Assembly HostSharedAssembly => typeof(HealthyFixtureType).Assembly;

    /// <summary>Gets the simple name of the assembly the tests declare shared.</summary>
    public static string SharedAssemblyName => HostSharedAssembly.GetName().Name!;

    /// <summary>Gets the major version of the assembly the tests declare shared.</summary>
    public static int SharedAssemblyMajorVersion => HostSharedAssembly.GetName().Version!.Major;

    /// <summary>Gets the policy entry declaring the fixture assembly shared by its unsigned identity and major version.</summary>
    public static SharedAssemblyPolicyEntry UnsignedPolicyEntry => new(SharedAssemblyName, string.Empty, SharedAssemblyMajorVersion);

    /// <summary>
    /// Installs the consumer package: its own assembly and its own copy of the shared assembly, both taken from the
    /// consumer project's build output.
    /// </summary>
    public static string CreateConsumerPackageInstall(DirectoryInfo root) =>
        CreateInstall(root, ConsumerPackageId, $"{ConsumerPackageId}.dll", $"{SharedAssemblyName}.dll");

    /// <summary>
    /// Installs the shared assembly's own package, as it is when acquired as an ordinary dependency: a package whose
    /// only assembly is its copy of the shared assembly.
    /// </summary>
    public static string CreateSharedAssemblyPackageInstall(DirectoryInfo root) =>
        CreateInstall(root, SharedAssemblyName, $"{SharedAssemblyName}.dll");

    /// <summary>Gets the consumer package's own assembly from the load context it was loaded into.</summary>
    public static Assembly ConsumerAssembly(AssemblyLoadContext context) =>
        context.Assemblies.Single(static assembly => assembly.GetName().Name == ConsumerPackageId);

    /// <summary>Gets the shared fixture type as <paramref name="consumerAssembly"/> binds it.</summary>
    public static Type BoundSharedType(Assembly consumerAssembly) =>
        (Type)consumerAssembly
            .GetType("Plugin.SharedConsumer.SharedConsumerMarker", throwOnError: true)!
            .GetProperty("SharedType")!
            .GetValue(null)!;

    private static string CreateInstall(DirectoryInfo root, string packageId, params string[] assemblyFileNames)
    {
        var installDirectory = root.CreateSubdirectory(packageId);
        foreach (var assemblyFileName in assemblyFileNames)
        {
            File.Copy(
                TestFixtureAssemblyPaths.FindProjectAssembly(ConsumerProjectDirectoryName, assemblyFileName),
                Path.Combine(installDirectory.FullName, assemblyFileName));
        }

        return installDirectory.FullName;
    }
}
