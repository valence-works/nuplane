using System.Text.Json;

namespace Nuplane.Store.State.ProtectionSerialization;

/// <summary>Rejects duplicate persisted fields before DTO deserialization can silently discard them.</summary>
internal static class PersistedJsonChecks
{
    internal static void EnsureUniqueProperties(JsonElement element, string recordName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new JsonException($"The {recordName} record contains duplicate property '{property.Name}'.");
                    EnsureUniqueProperties(property.Value, recordName);
                }
                break;
            }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    EnsureUniqueProperties(item, recordName);
                break;
        }
    }
}
