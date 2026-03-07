using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Hosting;

/// <summary>
/// A background service that queues startup and periodic reconciliation triggers.
/// Uses <see cref="ConvergenceOptions.PollInterval"/> when convergence is configured,
/// otherwise falls back to <see cref="ReconciliationOptions.PollInterval"/>.
/// Registered automatically when <see cref="ReconciliationOptions.EnableAutomaticReconciliation"/> is <see langword="true"/>.
/// </summary>
internal sealed class ReconciliationHostedService : BackgroundService
{
    private readonly IReconciliationTriggerIngress _triggerSink;
    private readonly ReconciliationOptions _options;
    private readonly ConvergenceOptions _convergenceOptions;
    private readonly ILogger<ReconciliationHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ReconciliationHostedService"/>.
    /// </summary>
    public ReconciliationHostedService(
        IReconciliationTriggerIngress triggerSink,
        IOptions<ReconciliationOptions> options,
        IOptions<ConvergenceOptions> convergenceOptions,
        ILogger<ReconciliationHostedService> logger)
    {
        _triggerSink = triggerSink ?? throw new ArgumentNullException(nameof(triggerSink));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _convergenceOptions = (convergenceOptions ?? throw new ArgumentNullException(nameof(convergenceOptions))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var effectivePollInterval = _convergenceOptions.Manifest.Enabled
            ? _convergenceOptions.PollInterval
            : _options.PollInterval;

        _logger.LogInformation("Nuplane automatic reconciliation started with poll interval {PollInterval}", effectivePollInterval);

        try
        {
            _triggerSink.Enqueue(ReconciliationTrigger.Startup());
            _logger.LogDebug("Startup reconciliation trigger queued");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue startup reconciliation trigger");
        }

        using var timer = new PeriodicTimer(effectivePollInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                _triggerSink.Enqueue(ReconciliationTrigger.Scheduled());
                _logger.LogDebug("Scheduled reconciliation trigger queued");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to queue scheduled reconciliation trigger");
            }
        }

        _logger.LogInformation("Nuplane automatic reconciliation stopped");
    }
}
