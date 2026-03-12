using Nuplane.Health;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Runtime.Tests.Health;

public sealed class ObservationDegradationTrackerTests
{
    [Fact]
    public void MarkDegraded_DistinctOrigins_TracksCurrentCount()
    {
        var tracker = new ObservationDegradationTracker();
        var directory = FeedObservationOrigin.DirectoryWatcher("local-feed");
        var polling = FeedObservationOrigin.Polling("remote-feed");

        tracker.MarkDegraded(directory);
        tracker.MarkDegraded(polling);

        Assert.Equal(2, tracker.DegradedCount);
        Assert.True(tracker.IsDegraded(directory));
        Assert.True(tracker.IsDegraded(polling));
    }

    [Fact]
    public void MarkDegraded_SameOriginTwice_DoesNotDoubleCount()
    {
        var tracker = new ObservationDegradationTracker();
        var origin = FeedObservationOrigin.DirectoryWatcher("local-feed");

        tracker.MarkDegraded(origin);
        tracker.MarkDegraded(origin);

        Assert.Equal(1, tracker.DegradedCount);
        Assert.Single(tracker.GetDegradedOrigins());
    }

    [Fact]
    public void MarkRecovered_RemovesOriginFromCurrentDegradedSet()
    {
        var tracker = new ObservationDegradationTracker();
        var origin = FeedObservationOrigin.DirectoryWatcher("local-feed");

        tracker.MarkDegraded(origin);
        tracker.MarkRecovered(origin);

        Assert.Equal(0, tracker.DegradedCount);
        Assert.False(tracker.IsDegraded(origin));
        Assert.Empty(tracker.GetDegradedOrigins());
    }
}
