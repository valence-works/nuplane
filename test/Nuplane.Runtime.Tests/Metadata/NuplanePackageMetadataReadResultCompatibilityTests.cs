using System.Text.Json;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Metadata;

namespace Nuplane.Runtime.Tests.Metadata;

public sealed class NuplanePackageMetadataReadResultCompatibilityTests
{
    [Fact]
    public void LegacyConstructionAndDeconstruction_PreserveFourPositionalMembers()
    {
        var result = new NuplanePackageMetadataReadResult(true, false, null, "invalid document");
        var (found, valid, metadata, diagnostic) = result;

        Assert.True(found);
        Assert.False(valid);
        Assert.Null(metadata);
        Assert.Equal("invalid document", diagnostic);
        Assert.Null(result.AdmissionRefusalReason);
    }

    [Theory]
    [InlineData(PackageStoreAdmissionReason.UnknownAuthority)]
    [InlineData(PackageStoreAdmissionReason.UnsupportedFilesystem)]
    [InlineData(PackageStoreAdmissionReason.RootMismatch)]
    [InlineData(PackageStoreAdmissionReason.IncompleteEnrollment)]
    [InlineData(PackageStoreAdmissionReason.StateMismatch)]
    [InlineData(PackageStoreAdmissionReason.ExpiredScope)]
    [InlineData(PackageStoreAdmissionReason.UnsupportedParticipant)]
    public void Refused_RemainsDistinctFromMissingAndInvalidAfterCopyAndJsonRoundTrip(
        PackageStoreAdmissionReason reason)
    {
        var result = NuplanePackageMetadataReadResult.Refused(reason);
        var copy = result with { };
        var restored = JsonSerializer.Deserialize<NuplanePackageMetadataReadResult>(JsonSerializer.Serialize(copy));

        Assert.Equal(result, restored);
        Assert.NotEqual(NuplanePackageMetadataReadResult.Missing, restored);
        Assert.NotEqual(NuplanePackageMetadataReadResult.Invalid(result.Diagnostic!), restored);
        Assert.Equal(reason, restored!.AdmissionRefusalReason);
        Assert.False(restored.MetadataFound);
        Assert.False(restored.IsValid);
        Assert.Null(restored.Metadata);
        Assert.Null(restored.DeclaredSchemaVersion);
    }

    [Fact]
    public void Refused_UnknownReasonIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NuplanePackageMetadataReadResult.Refused((PackageStoreAdmissionReason)int.MaxValue));
    }
}
