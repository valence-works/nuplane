namespace Nuplane.Runtime.Reconciliation.Models;

/// <summary>
/// Identifies the feed and observation mechanism responsible for an observed-change trigger.
/// Also serves as the stable identity for observation monitor health tracking.
/// </summary>
public sealed record FeedObservationOrigin
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FeedObservationOrigin"/> class.
    /// </summary>
    /// <param name="feedName">The observed feed name.</param>
    /// <param name="kind">The observation mechanism.</param>
    public FeedObservationOrigin(string feedName, FeedObservationKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedName);
        FeedName = feedName;
        Kind = kind;
    }

    /// <summary>Gets the observed feed name.</summary>
    public string FeedName { get; }

    /// <summary>Gets the observation mechanism.</summary>
    public FeedObservationKind Kind { get; }

    /// <summary>Creates a directory-watcher origin for the specified feed.</summary>
    public static FeedObservationOrigin DirectoryWatcher(string feedName) =>
        new(feedName, FeedObservationKind.DirectoryWatcher);

    /// <summary>Creates a notification origin for the specified feed.</summary>
    public static FeedObservationOrigin Notification(string feedName) =>
        new(feedName, FeedObservationKind.Notification);

    /// <summary>Creates a polling origin for the specified feed.</summary>
    public static FeedObservationOrigin Polling(string feedName) =>
        new(feedName, FeedObservationKind.Polling);

    /// <inheritdoc />
    public override string ToString() => $"{Kind}:{FeedName}";
}

