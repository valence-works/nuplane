using System.Reflection;
using Nuplane.Observability;

namespace Nuplane.Runtime.Tests.Observability;

/// <summary>
/// Pins the one property of source-generated logging that nothing else checks: a log event id
/// identifies a message within an assembly, not within the type that happens to declare it. Two
/// <c>[LoggerMessage]</c> methods in different types of the same assembly sharing an id produces no
/// compiler diagnostic and no runtime failure — it silently makes the id useless for filtering,
/// alerting, and correlating, which is exactly the kind of defect that is only ever found by reading
/// logs much later.
/// <para>
/// Only methods that declare an <c>EventId</c> are compared, because only a declared id is a
/// promise about which id a message carries; a few store logs deliberately leave theirs to the
/// generator, and the attribute then carries nothing to compare.
/// </para>
/// </summary>
public sealed class LoggerMessageEventIdUniquenessTests
{
    private const string LoggerMessageAttributeName = "Microsoft.Extensions.Logging.LoggerMessageAttribute";

    private static readonly Assembly NuplaneAssembly = typeof(ReconciliationLogger).Assembly;

    [Fact]
    public void LoggerMessageMethods_AcrossTheNuplaneAssembly_UseDistinctEventIds()
    {
        var duplicates = LoggerMessageMethods()
            .GroupBy(static method => method.EventId)
            .Where(static group => group.Skip(1).Any())
            .Select(static group =>
                $"{group.Key}: {string.Join(", ", group.Select(static method => $"{method.DeclaringType}.{method.Name}").Order(StringComparer.Ordinal))}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void LoggerMessageMethods_AcrossTheNuplaneAssembly_AreFound()
    {
        // Guards the reflection itself: a source-generator or attribute change that stopped these
        // methods being discoverable would turn both checks above into vacuous passes.
        Assert.NotEmpty(LoggerMessageMethods());
        Assert.Contains(
            LoggerMessageMethods(),
            static method => method.DeclaringType == typeof(ReconciliationLogger));
    }

    private static (Type? DeclaringType, string Name, int EventId)[] LoggerMessageMethods() =>
        AllLoggerMessageMethods()
            .Select(static method => (method.DeclaringType, method.Name, EventId: TryReadEventId(method, out var eventId) ? eventId : (int?)null))
            .Where(static method => method.EventId is not null)
            .Select(static method => (method.DeclaringType, method.Name, method.EventId!.Value))
            .ToArray();

    private static IEnumerable<MethodInfo> AllLoggerMessageMethods() =>
        NuplaneAssembly
            .GetTypes()
            .SelectMany(static type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(static method => method.GetCustomAttributesData()
                .Any(static attribute => attribute.AttributeType.FullName == LoggerMessageAttributeName));

    /// <summary>
    /// Reads the event id as it was <i>written</i>, from the attribute's named arguments, rather than
    /// from a materialized attribute instance — an unset <c>EventId</c> would otherwise read as the
    /// perfectly valid id <c>0</c> and be indistinguishable from one that was set to it.
    /// </summary>
    private static bool TryReadEventId(MethodInfo method, out int eventId)
    {
        var argument = method.GetCustomAttributesData()
            .Where(static attribute => attribute.AttributeType.FullName == LoggerMessageAttributeName)
            .SelectMany(static attribute => attribute.NamedArguments)
            .Where(static argument => argument.MemberName == nameof(Microsoft.Extensions.Logging.LoggerMessageAttribute.EventId))
            .Select(static argument => argument.TypedValue.Value)
            .FirstOrDefault();

        if (argument is int value)
        {
            eventId = value;
            return true;
        }

        eventId = default;
        return false;
    }
}
