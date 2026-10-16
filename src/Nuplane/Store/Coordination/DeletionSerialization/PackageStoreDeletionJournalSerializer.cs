using System.Text.Json;
using System.Text.Json.Serialization;
using Nuplane.Store.Coordination.DeletionRecords;
using Nuplane.Store.State.ProtectionSerialization;

namespace Nuplane.Store.Coordination.DeletionSerialization;

/// <summary>Strictly serializes bounded descriptive package-store deletion journals.</summary>
/// <remarks>Journal integrity is descriptive; each deletion invocation must establish current native authority independently.</remarks>
internal sealed class PackageStoreDeletionJournalSerializer
{
    internal const int MaximumPayloadBytes = 16 * 1024 * 1024;

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 64 };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        MaxDepth = 64
    };

    internal byte[] Serialize(PackageStoreDeletionJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ValidateIntegrity(journal, deserializing: false);

        using var payload = new BoundedControlPayloadStream(MaximumPayloadBytes);
        try
        {
            JsonSerializer.Serialize(payload, PackageStoreDeletionJournalDto.FromJournal(journal), JsonOptions);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("The deletion-journal payload exceeds the supported size limit.", exception);
        }
        return payload.ToArray();
    }

    internal PackageStoreDeletionJournal Deserialize(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0 || payload.Length > MaximumPayloadBytes)
            throw new JsonException("The deletion-journal payload is empty or exceeds the supported size limit.");

        try
        {
            using var document = JsonDocument.Parse(payload, DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("A deletion-journal payload must be a JSON object.");

            PersistedJsonChecks.EnsureUniqueProperties(document.RootElement, "deletion-journal");
            ValidateRequiredShape(document.RootElement);
            var dto = document.RootElement.Deserialize<PackageStoreDeletionJournalDto>(JsonOptions)
                ?? throw new JsonException("The deletion-journal payload cannot be null.");
            var journal = dto.ToJournal();
            ValidateIntegrity(journal, deserializing: true);
            return journal;
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new JsonException("The deletion-journal payload is malformed or inconsistent.", exception);
        }
    }

    private static void ValidateIntegrity(PackageStoreDeletionJournal journal, bool deserializing)
    {
        ProtectionDigest.ValidateCanonicalDigest(journal.IntegrityDigest);
        var actual = ProtectionDigest.PackageStoreDeletion(journal);
        if (!string.Equals(journal.IntegrityDigest, actual, StringComparison.Ordinal))
        {
            if (deserializing)
                throw new JsonException("The deletion-journal digest does not match its contents.");
            throw new InvalidOperationException("The deletion-journal digest does not match its contents.");
        }
    }

    private static void ValidateRequiredShape(JsonElement root)
    {
        RequireObject(root, "deletion journal", "schemaVersion", "storeRoot", "enrollmentEpoch", "operationId",
            "candidateId", "originalInstall", "packageParentRelativeComponents", "packageParentIdentity",
            "packageParentSemantics", "originalBasename", "quarantineBasename", "completionMarkerIdentity",
            "keepNewestInactiveVersionsPerPackage", "phase", "manifest", "integrityDigest");

        ValidateRootIdentity(RequireObjectProperty(root, "storeRoot", "deletion journal"), "store root");
        ValidateInstallIdentity(RequireObjectProperty(root, "originalInstall", "deletion journal"));
        ValidatePhysicalIdentity(RequireObjectProperty(root, "packageParentIdentity", "deletion journal"), "package parent identity");
        ValidateNameSemantics(RequireObjectProperty(root, "packageParentSemantics", "deletion journal"), "package parent name profile");
        ValidatePhysicalIdentity(RequireObjectProperty(root, "completionMarkerIdentity", "deletion journal"), "completion-marker identity");

        var parentComponents = RequireArrayProperty(root, "packageParentRelativeComponents", "deletion journal");
        if (parentComponents.GetArrayLength() != 2)
            throw new JsonException("The package-parent locator must contain exactly two components.");
        foreach (var component in parentComponents.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.String)
                throw new JsonException("The package-parent locator must contain only strings.");
        }

        ValidateManifest(RequireObjectProperty(root, "manifest", "deletion journal"));
    }

    private static void ValidateInstallIdentity(JsonElement install)
    {
        RequireObject(install, "install identity", "root", "packageId", "version", "rootRelativeInstallPath",
            "directoryIdentity", "completionIdentity", "verifiedArchiveHash");
        ValidateRootIdentity(RequireObjectProperty(install, "root", "install identity"), "install root");
        ValidatePhysicalIdentity(RequireObjectProperty(install, "directoryIdentity", "install identity"), "install directory identity");
    }

    private static void ValidateManifest(JsonElement manifest)
    {
        RequireObject(manifest, "deletion manifest", "installDirectoryIdentity", "installDirectorySemantics", "entries");
        ValidatePhysicalIdentity(RequireObjectProperty(manifest, "installDirectoryIdentity", "deletion manifest"), "install-directory identity");
        ValidateNameSemantics(RequireObjectProperty(manifest, "installDirectorySemantics", "deletion manifest"), "install-directory name profile");

        var entries = RequireArrayProperty(manifest, "entries", "deletion manifest");
        if (entries.GetArrayLength() > PhysicalStoreDeletionManifest.MaximumEntries)
            throw new JsonException("The deletion manifest exceeds its entry-count limit.");

        var directChildCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            RequireObject(entry, "deletion manifest entry", "relativeComponents", "kind", "identity", "parentIdentity",
                "parentSemantics", "directorySemantics");
            ValidatePhysicalIdentity(RequireObjectProperty(entry, "identity", "deletion manifest entry"), "entry identity");
            ValidatePhysicalIdentity(RequireObjectProperty(entry, "parentIdentity", "deletion manifest entry"), "entry parent identity");
            ValidateNameSemantics(RequireObjectProperty(entry, "parentSemantics", "deletion manifest entry"), "entry parent name profile");

            var components = RequireArrayProperty(entry, "relativeComponents", "deletion manifest entry");
            if (components.GetArrayLength() is 0 or > PhysicalStoreDeletionManifest.MaximumDepth)
                throw new JsonException("A deletion-manifest path has an invalid depth.");
            var pathComponents = new string[components.GetArrayLength()];
            var componentIndex = 0;
            foreach (var component in components.EnumerateArray())
            {
                if (component.ValueKind != JsonValueKind.String)
                    throw new JsonException("A deletion-manifest path must contain only strings.");
                pathComponents[componentIndex++] = component.GetString()!;
            }

            var parentPath = string.Join("/", pathComponents, 0, pathComponents.Length - 1);
            directChildCounts.TryGetValue(parentPath, out var childCount);
            childCount++;
            if (childCount > PhysicalStoreDeletionManifest.MaximumChildrenPerDirectory)
                throw new JsonException("A deletion-manifest directory exceeds its direct-child limit.");
            directChildCounts[parentPath] = childCount;

            var directorySemantics = RequiredProperty(entry, "directorySemantics", "deletion manifest entry");
            if (directorySemantics.ValueKind != JsonValueKind.Null)
                ValidateNameSemantics(directorySemantics, "entry directory name profile");
        }
    }

    private static void ValidateRootIdentity(JsonElement root, string name)
    {
        RequireObject(root, name, "handleIdentity");
        ValidatePhysicalIdentity(RequireObjectProperty(root, "handleIdentity", name), $"{name} handle identity");
    }

    private static void ValidatePhysicalIdentity(JsonElement identity, string name)
        => RequireObject(identity, name, "provider", "volumeOrDeviceId", "fileId");

    private static void ValidateNameSemantics(JsonElement semantics, string name)
        => RequireObject(semantics, name, "profileId", "encoding", "caseSensitive", "normalizationInsensitive");

    private static void RequireObject(JsonElement element, string name, params string[] requiredProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException($"The {name} must be a JSON object.");

        foreach (var property in requiredProperties)
        {
            if (!element.TryGetProperty(property, out _))
                throw new JsonException($"The {name} is missing '{property}'.");
        }
    }

    private static JsonElement RequireObjectProperty(JsonElement parent, string propertyName, string parentName)
    {
        var value = RequiredProperty(parent, propertyName, parentName);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException($"The {parentName} '{propertyName}' must be a non-null JSON object.");
        return value;
    }

    private static JsonElement RequireArrayProperty(JsonElement parent, string propertyName, string parentName)
    {
        var value = RequiredProperty(parent, propertyName, parentName);
        if (value.ValueKind != JsonValueKind.Array)
            throw new JsonException($"The {parentName} '{propertyName}' must be a JSON array.");
        return value;
    }

    private static JsonElement RequiredProperty(JsonElement parent, string propertyName, string parentName)
        => parent.TryGetProperty(propertyName, out var value)
            ? value
            : throw new JsonException($"The {parentName} is missing '{propertyName}'.");
}
