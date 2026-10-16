using System.Text.Json;

namespace Nuplane.Store.Coordination.DeletionSerialization;

internal static class DeletionDtoChecks
{
    internal const string JournalOwner = "deletion journal";
    internal const string ManifestOwner = "deletion manifest";
    internal const string ManifestEntryOwner = "deletion manifest entry";
    internal const string NameProfileOwner = "native name profile";

    internal static T RequiredValue<T>(T? value, string name, string owner) where T : struct
        => value ?? throw Missing(name, owner);

    internal static T RequiredReference<T>(T? value, string name, string owner) where T : class
        => value ?? throw Missing(name, owner);

    internal static List<TResult> MapRequired<TSource, TResult>(
        List<TSource?>? values,
        string name,
        string owner,
        Func<TSource, TResult> map,
        string? nullItemMessage = null)
        where TSource : class
    {
        if (values is null)
            throw Missing(name, owner);

        var mapped = new List<TResult>(values.Count);
        foreach (var value in values)
        {
            if (value is null)
                throw new JsonException(nullItemMessage ?? $"The {owner} '{name}' array cannot contain null items.");
            mapped.Add(map(value));
        }

        return mapped;
    }

    internal static JsonException Missing(string name, string owner)
        => new($"The {owner} is missing '{name}'.");
}
