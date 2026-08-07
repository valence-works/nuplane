using Nuplane.Operational;

namespace Nuplane.Hosting;

internal sealed class StartupRecoveryOperationalStateContributor(StartupRecoveryState state) : IOperationalStateContributor
{
    private readonly StartupRecoveryState _state = state ?? throw new ArgumentNullException(nameof(state));

    public Task<OperationalStateContribution> ContributeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_state.GetContribution());
}
