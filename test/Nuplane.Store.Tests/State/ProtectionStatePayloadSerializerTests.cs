using System.Text;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;
using GraphFixture = Nuplane.Store.Tests.Coordination.PackageProtectionRecordTests.GraphFixture;

namespace Nuplane.Store.Tests.State;

public sealed class ProtectionStatePayloadSerializerTests : IDisposable
{
    private readonly PackageStoreFixture _fixture = new();
    private readonly StoreStateSerializer _serializer = new();

    [Fact]
    public async Task WriteAndReadPayloadAsync_StreamOnlyRoundTripsIndependentActiveAndLkgProtection()
    {
        var expected = State(Protection());
        Assert.IsAssignableFrom<IPackageProtectionStatePayloadSerializer>(_serializer);
        await using var payload = new MemoryStream();

        await _serializer.WritePayloadAsync(payload, expected, CancellationToken.None);
        Assert.True(payload.CanRead);
        Assert.True(payload.CanWrite);

        payload.Position = 0;
        var actual = await _serializer.ReadPayloadAsync(payload, CancellationToken.None);

        Assert.True(payload.CanRead);
        var protection = Assert.IsType<PackageProtectionRecord>(actual.ProtectionRecord);
        Assert.True(expected.ProtectionRecord!.HasSamePayloadAs(protection));
        Assert.Equal("2.0.0", Assert.Single(protection.ActiveClosure.Graphs!).Nodes[0].Install.Version);
        Assert.Equal("1.0.0", Assert.Single(protection.RecoverableClosure.Graphs!).Nodes[0].Install.Version);
    }

    [Fact]
    public async Task ReadPayloadAsync_DuplicateOuterProtectionField_Refuses()
    {
        var json = await SerializeAsync(State(Protection()));
        var duplicate = json.Replace(
            "\"protection\": {",
            "\"protection\": null, \"protection\": {",
            StringComparison.Ordinal);
        Assert.NotSame(json, duplicate);

        await using var payload = new MemoryStream(Encoding.UTF8.GetBytes(duplicate));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(
            () => _serializer.ReadPayloadAsync(payload, CancellationToken.None));
        Assert.True(payload.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadPayloadAsync_LegacyAbsentOrNullProtection_RemainsUnknown(bool explicitNull)
    {
        var json = await SerializeAsync(State(protection: null));
        if (explicitNull)
            json = json.Replace("\"updatedAt\":", "\"protection\": null, \"updatedAt\":", StringComparison.Ordinal);

        await using var payload = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var loaded = await _serializer.ReadPayloadAsync(payload, CancellationToken.None);

        Assert.Null(loaded.ProtectionRecord);
    }

    [Fact]
    public async Task ReadAndWritePayloadAsync_NormalizesOnlyOptionalLegacyMaps()
    {
        var nullMaps = State(null) with { ActivePackageDescriptorsById = null, ActiveGraphsById = null };
        var explicitEmptyMaps = nullMaps with
        {
            ActivePackageDescriptorsById = new(StringComparer.OrdinalIgnoreCase),
            ActiveGraphsById = new(StringComparer.OrdinalIgnoreCase)
        };

        await using var nullPayload = new MemoryStream();
        await using var emptyPayload = new MemoryStream();
        await _serializer.WritePayloadAsync(nullPayload, nullMaps, CancellationToken.None);
        await _serializer.WritePayloadAsync(emptyPayload, explicitEmptyMaps, CancellationToken.None);
        Assert.Equal(nullPayload.ToArray(), emptyPayload.ToArray());

        nullPayload.Position = 0;
        var loaded = await _serializer.ReadPayloadAsync(nullPayload, CancellationToken.None);
        Assert.Empty(loaded.ActivePackageDescriptorsByIdNormalized);
        Assert.Empty(loaded.ActiveGraphsByIdNormalized);
    }

    [Fact]
    public async Task ReadAndWritePayloadAsync_CancellationPropagatesAndStreamsRemainOpen()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(await SerializeAsync(State(null))));
        await using var output = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _serializer.ReadPayloadAsync(input, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _serializer.WritePayloadAsync(output, State(null), cancellation.Token));
        Assert.True(input.CanRead);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task SaveAsync_AndWritePayloadAsync_UseTheSameWirePayload()
    {
        var state = State(Protection());
        await _serializer.SaveAsync(_fixture.StateFilePath, state, CancellationToken.None);
        var pathBytes = await File.ReadAllBytesAsync(_fixture.StateFilePath);
        await using var payload = new MemoryStream();

        await _serializer.WritePayloadAsync(payload, state, CancellationToken.None);

        Assert.Equal(pathBytes, payload.ToArray());
    }

    public void Dispose() => _fixture.Dispose();

    private async Task<string> SerializeAsync(StoreStateRecord state)
    {
        await using var stream = new MemoryStream();
        await _serializer.WritePayloadAsync(stream, state, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static PackageProtectionRecord Protection()
    {
        var active = new GraphFixture("2.0.0").Graph(ProtectedGraphDisposition.Active);
        var lkg = new GraphFixture("1.0.0").Graph(ProtectedGraphDisposition.Recoverable);
        var knownActive = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, [active]);
        var knownLkg = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, [lkg]);
        return new PackageProtectionRecord(
            1,
            active.Roots[0],
            1,
            "member",
            1,
            new string('a', 64),
            new string('b', 64),
            knownActive,
            knownLkg,
            [],
            legacyUnknownRecovery: false);
    }

    private static StoreStateRecord State(PackageProtectionRecord? protection)
    {
        var now = DateTimeOffset.Parse("2026-10-09T08:00:00+02:00");
        return new StoreStateRecord(
            new(StringComparer.OrdinalIgnoreCase) { ["Module"] = "2.0.0" },
            new(StringComparer.OrdinalIgnoreCase) { ["Module"] = "1.0.0" },
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase),
            now,
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase))
        {
            ProtectionRecord = protection
        };
    }
}
