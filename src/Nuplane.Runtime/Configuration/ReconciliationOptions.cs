using System;

namespace Nuplane.Runtime.Configuration;

public sealed class ReconciliationOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);

    public bool EnableSingleFlight { get; set; } = true;

    public int MaxRetryAttempts { get; set; } = 3;

    public TimeSpan InitialRetryBackoff { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxRetryBackoff { get; set; } = TimeSpan.FromSeconds(30);

    public bool IsValid() =>
        PollInterval > TimeSpan.Zero &&
        MaxRetryAttempts >= 0 &&
        InitialRetryBackoff > TimeSpan.Zero &&
        MaxRetryBackoff >= InitialRetryBackoff;
}
