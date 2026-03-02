using System;

namespace Nuplane.Runtime.Configuration;

public sealed class ReconciliationOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(60);

    public bool EnableSingleFlight { get; init; } = true;

    public int MaxRetryAttempts { get; init; } = 3;

    public TimeSpan InitialRetryBackoff { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxRetryBackoff { get; init; } = TimeSpan.FromSeconds(30);

    public bool IsValid() =>
        PollInterval > TimeSpan.Zero &&
        MaxRetryAttempts >= 0 &&
        InitialRetryBackoff > TimeSpan.Zero &&
        MaxRetryBackoff >= InitialRetryBackoff;
}
