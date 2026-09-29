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

    /// <summary>
    /// Gets the shared assembly from <paramref name="assemblies"/>; there must be exactly one, so a test that
    /// expects the host's copy, or the package's, does not pass on a list holding both.
    /// </summary>
    public static Assembly SharedAssemblyIn(IEnumerable<Assembly> assemblies) =>
        Assert.Single(SharedAssembliesIn(assemblies));

    /// <summary>Gets every assembly of <paramref name="assemblies"/> that carries the shared assembly's name.</summary>
    public static IEnumerable<Assembly> SharedAssembliesIn(IEnumerable<Assembly> assemblies) =>
        assemblies.Where(static assembly => assembly.GetName().Name == SharedAssemblyName);

    /// <summary>
    /// Installs the consumer package with one more assembly the host has no copy of, and returns the policy entry
    /// that declares that assembly shared.
    /// </summary>
    public static (string InstallPath, string AbsentAssemblyName, SharedAssemblyPolicyEntry Policy) CreateConsumerPackageInstallWithHostAbsentAssembly(DirectoryInfo root)
    {
        var installPath = CreateConsumerPackageInstall(root);
        var absent = HostFreeLoadTestSupport.EmitPackage(root, "Shared.Absent");
        var absentFileName = $"{absent.AssemblyName}.dll";
        File.Copy(Path.Combine(absent.InstallPath, absentFileName), Path.Combine(installPath, absentFileName));

        return (installPath, absent.AssemblyName, new SharedAssemblyPolicyEntry(absent.AssemblyName, string.Empty, 1));
    }

    /// <summary>
    /// Installs a package carrying the shared assembly's name at a major version one below the host's copy, and
    /// returns the policy entry that declares that lower major shared. The host has a copy of the assembly, but not
    /// of that major version, and the default binder would hand the higher one back.
    /// </summary>
    public static (string InstallPath, SharedAssemblyPolicyEntry Policy) CreateSharedAssemblyPackageInstallOfLowerMajor(DirectoryInfo root)
    {
        var lowerMajor = SharedAssemblyMajorVersion - 1;
        var installDirectory = root.CreateSubdirectory($"{SharedAssemblyName}-lower-major");
        HostFreeLoadTestSupport.EmitAssembly(
            SharedAssemblyName,
            new Version(lowerMajor, 9, 0, 0),
            Path.Combine(installDirectory.FullName, $"{SharedAssemblyName}.dll"));

        return (installDirectory.FullName, new SharedAssemblyPolicyEntry(SharedAssemblyName, string.Empty, lowerMajor));
    }

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
