using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.TestSupport;

/// <summary>
/// An <see cref="IFailureRecorder"/> that keeps every failure a cycle records, so a test can assert
/// which package failed at which stage with which message.
/// </summary>
internal sealed class RecordingFailureRecorder : IFailureRecorder
{
    public List<FailureRecord> Records { get; } = [];

    public Task RecordAsync(string packageId, string stage, string message, string correlationId, CancellationToken cancellationToken)
    {
        Records.Add(new(packageId, stage, message, DateTimeOffset.UtcNow, correlationId));
        return Task.CompletedTask;
    }

    /// <summary>The message recorded for <paramref name="packageId"/> at <paramref name="stage"/>.</summary>
    public string MessageFor(string packageId, string stage) =>
        Records
            .Single(record => record.PackageId == packageId && record.Stage == stage)
            .Message;
}
