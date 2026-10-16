using Nuplane.Integration.Tests.Fixtures;

namespace Nuplane.Integration.Tests;

public sealed class PackageStoreParticipantProcessTests
{
    [Fact]
    public async Task ReleaseAsync_AfterReadiness_CapturesSuccessfulChildExit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var participant = await PackageStoreParticipantProcess.StartAsync("before read \"quoted\"", timeout.Token);
        Assert.False(participant.HasExited);

        var result = await participant.ReleaseAsync(timeout.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"kind\":\"ready\"", result.StandardOutput);
        Assert.Contains("\"kind\":\"released\"", result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.True(participant.HasExited);
    }

    [Fact]
    public async Task DisposeAsync_BeforeRelease_TerminatesOwnedChildAndIsIdempotent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var participant = await PackageStoreParticipantProcess.StartAsync("unreleased", timeout.Token);
        Assert.False(participant.HasExited);

        await participant.DisposeAsync();
        await participant.DisposeAsync();

        Assert.True(participant.HasExited);
    }

    [Fact]
    public async Task ReleaseAsync_WhenCancelled_LeavesGateWaitingUntilOwnedTermination()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var participant = await PackageStoreParticipantProcess.StartAsync("cancelled", timeout.Token);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => participant.ReleaseAsync(cancelled.Token));
        Assert.False(participant.HasExited);
        var result = await participant.TerminateAsync(timeout.Token);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("\"kind\":\"released\"", result.StandardOutput);
        Assert.True(participant.HasExited);
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyCancelled_DoesNotStartAChild()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PackageStoreParticipantProcess.StartAsync("not-started", cancelled.Token));
    }
}
