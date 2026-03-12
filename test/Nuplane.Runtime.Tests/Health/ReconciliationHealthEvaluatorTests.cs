using Nuplane.Health;

namespace Nuplane.Runtime.Tests.Health;

public sealed class ReconciliationHealthEvaluatorTests
{
    private readonly ReconciliationHealthEvaluator _sut = new();

    [Fact]
    public void Evaluate_NoFailures_NotDegraded()
    {
        var input = new ReconciliationHealthInput(
            HadAnyFailures: false,
            AllSourcesFresh: true,
            LockFailures: 0,
            CleanupFailures: 0);

        var result = _sut.Evaluate(input);

        Assert.False(result);
        Assert.False(_sut.IsDegraded);
    }

    [Fact]
    public void Evaluate_StaleSource_Degraded()
    {
        var input = new ReconciliationHealthInput(
            HadAnyFailures: false,
            AllSourcesFresh: false,
            LockFailures: 0,
            CleanupFailures: 0);

        var result = _sut.Evaluate(input);

        Assert.True(result);
        Assert.True(_sut.IsDegraded);
    }

    [Fact]
    public void Evaluate_ManifestFailures_Degraded()
    {
        var input = new ReconciliationHealthInput(
            HadAnyFailures: false,
            AllSourcesFresh: true,
            LockFailures: 0,
            CleanupFailures: 0,
            ManifestFailures: 2);

        var result = _sut.Evaluate(input);

        Assert.True(result);
        Assert.Equal(2, _sut.LastManifestFailureCount);
    }

    [Fact]
    public void Evaluate_SourceOutages_Degraded()
    {
        var input = new ReconciliationHealthInput(
            HadAnyFailures: false,
            AllSourcesFresh: true,
            LockFailures: 0,
            CleanupFailures: 0,
            SourceOutages: 1);

        var result = _sut.Evaluate(input);

        Assert.True(result);
        Assert.Equal(1, _sut.LastSourceOutageCount);
    }

    [Fact]
    public void Evaluate_AcquisitionFailures_Degraded()
    {
        var input = new ReconciliationHealthInput(
            HadAnyFailures: false,
            AllSourcesFresh: true,
            LockFailures: 0,
            CleanupFailures: 0,
            AcquisitionFailures: 3);

        var result = _sut.Evaluate(input);

        Assert.True(result);
        Assert.Equal(3, _sut.LastAcquisitionFailureCount);
    }

    [Fact]
    public void Evaluate_AdminRejections_Degraded()
    {
        var input = new ReconciliationHealthInput(
            HadAnyFailures: false,
            AllSourcesFresh: true,
            LockFailures: 0,
            CleanupFailures: 0,
            AdminRejections: 2);

        var result = _sut.Evaluate(input);

        Assert.True(result);
        Assert.Equal(2, _sut.LastAdminRejectionCount);
    }

    [Fact]
    public void Evaluate_NegativeValues_ClampedToZero()
    {
        var input = new ReconciliationHealthInput(
            HadAnyFailures: false,
            AllSourcesFresh: true,
            LockFailures: -1,
            CleanupFailures: -1,
            ManifestFailures: -1,
            SourceOutages: -1,
            AcquisitionFailures: -1,
            AdminRejections: -1);

        _sut.Evaluate(input);

        Assert.Equal(0, _sut.LastLockFailureCount);
        Assert.Equal(0, _sut.LastCleanupFailureCount);
        Assert.Equal(0, _sut.LastManifestFailureCount);
        Assert.Equal(0, _sut.LastSourceOutageCount);
        Assert.Equal(0, _sut.LastAcquisitionFailureCount);
        Assert.Equal(0, _sut.LastAdminRejectionCount);
    }

    [Fact]
    public void Evaluate_MultipleConvergenceFailures_AllTracked()
    {
        var input = new ReconciliationHealthInput(
            HadAnyFailures: true,
            AllSourcesFresh: false,
            LockFailures: 2,
            CleanupFailures: 3,
            ManifestFailures: 5,
            SourceOutages: 6,
            AcquisitionFailures: 7,
            AdminRejections: 9);

        _sut.Evaluate(input);

        Assert.True(_sut.IsDegraded);
        Assert.Equal(2, _sut.LastLockFailureCount);
        Assert.Equal(3, _sut.LastCleanupFailureCount);
        Assert.Equal(5, _sut.LastManifestFailureCount);
        Assert.Equal(6, _sut.LastSourceOutageCount);
        Assert.Equal(7, _sut.LastAcquisitionFailureCount);
        Assert.Equal(9, _sut.LastAdminRejectionCount);
    }

    [Fact]
    public void Evaluate_DefaultConvergenceFields_DoNotDegrade()
    {
        // Using only positional parameters — convergence fields default to 0
        var input = new ReconciliationHealthInput(
            HadAnyFailures: false,
            AllSourcesFresh: true,
            LockFailures: 0,
            CleanupFailures: 0);

        var result = _sut.Evaluate(input);

        Assert.False(result);
        Assert.Equal(0, _sut.LastManifestFailureCount);
        Assert.Equal(0, _sut.LastSourceOutageCount);
        Assert.Equal(0, _sut.LastAcquisitionFailureCount);
        Assert.Equal(0, _sut.LastAdminRejectionCount);
    }

    [Fact]
    public void Evaluate_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _sut.Evaluate(null!));
    }
}
