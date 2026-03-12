using NSubstitute;
using Nuplane.Abstractions;
using Nuplane.Feeds;
using Nuplane.Observability;
using Nuplane.Reconciliation;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class ReconciliationRollbackCoordinatorTests
{
    private readonly IReconciliationLogger _logger = Substitute.For<IReconciliationLogger>();
    private readonly ReconciliationRollbackCoordinator _sut;

    public ReconciliationRollbackCoordinatorTests()
    {
        _sut = new(_logger);
    }

    [Fact]
    public void EvaluateAndRollback_AllSucceeded_NoRollbackPerformed()
    {
        var outcomes = new[]
        {
            Outcome("pkg-a", PackageOperationStatus.Succeeded),
            Outcome("pkg-b", PackageOperationStatus.Succeeded)
        };

        var result = _sut.EvaluateAndRollback("corr-1", outcomes);

        Assert.False(result.RollbackPerformed);
        Assert.Empty(result.RolledBackPackages);
        Assert.Empty(result.PreservedPackages);
        Assert.Equal(2, result.SucceededPackages.Count);
        Assert.Equal(ConvergenceReasonCodes.RollbackNotRequired, result.ReasonCode);
    }

    [Fact]
    public void EvaluateAndRollback_OneFailed_RollbackPerformed()
    {
        var outcomes = new[]
        {
            Outcome("pkg-a", PackageOperationStatus.Succeeded),
            Outcome("pkg-b", PackageOperationStatus.Failed)
        };

        var result = _sut.EvaluateAndRollback("corr-1", outcomes);

        Assert.True(result.RollbackPerformed);
        Assert.Contains("pkg-b", result.RolledBackPackages);
        Assert.Contains("pkg-a", result.SucceededPackages);
        Assert.Equal(ConvergenceReasonCodes.RollbackPerformed, result.ReasonCode);
    }

    [Fact]
    public void EvaluateAndRollback_SkippedPackages_ArePreserved()
    {
        var outcomes = new[]
        {
            Outcome("pkg-a", PackageOperationStatus.Succeeded),
            Outcome("pkg-b", PackageOperationStatus.Skipped)
        };

        var result = _sut.EvaluateAndRollback("corr-1", outcomes);

        Assert.False(result.RollbackPerformed);
        Assert.Contains("pkg-b", result.PreservedPackages);
        Assert.Contains("pkg-a", result.SucceededPackages);
    }

    [Fact]
    public void EvaluateAndRollback_MixedOutcomes_CategorizesCorrectly()
    {
        var outcomes = new[]
        {
            Outcome("pkg-a", PackageOperationStatus.Succeeded),
            Outcome("pkg-b", PackageOperationStatus.Failed),
            Outcome("pkg-c", PackageOperationStatus.Skipped),
            Outcome("pkg-d", PackageOperationStatus.Failed)
        };

        var result = _sut.EvaluateAndRollback("corr-1", outcomes);

        Assert.True(result.RollbackPerformed);
        Assert.Single(result.SucceededPackages);
        Assert.Equal(2, result.RolledBackPackages.Count);
        Assert.Single(result.PreservedPackages);
    }

    [Fact]
    public void EvaluateAndRollback_EmptyOutcomes_NoRollbackPerformed()
    {
        var result = _sut.EvaluateAndRollback("corr-1", []);

        Assert.False(result.RollbackPerformed);
        Assert.Empty(result.RolledBackPackages);
        Assert.Empty(result.PreservedPackages);
        Assert.Empty(result.SucceededPackages);
    }

    [Fact]
    public void EvaluateAndRollback_FailedPackage_LogsCycleCompletedDegraded()
    {
        var outcomes = new[]
        {
            Outcome("pkg-a", PackageOperationStatus.Failed)
        };

        _sut.EvaluateAndRollback("corr-1", outcomes);

        _logger.Received().LogCycleCompleted("corr-1", degraded: true, failedCount: 1);
    }

    [Fact]
    public void EvaluateAndRollback_NullCorrelationId_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            _sut.EvaluateAndRollback(null!, []));
    }

    [Fact]
    public void EvaluateAndRollback_NullOutcomes_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _sut.EvaluateAndRollback("corr-1", null!));
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ReconciliationRollbackCoordinator(null!));
    }

    private static AcquisitionOutcomeEntry Outcome(string packageId, PackageOperationStatus status) =>
        new(packageId, "1.0.0", AcquisitionStage.Download, status, "test");
}
