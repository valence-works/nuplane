namespace Nuplane.Runtime.Reconciliation.Middleware;

internal sealed class ReconciliationPipeline
{
    private readonly List<IReconciliationMiddleware> _middlewares = [];

    public ReconciliationPipeline Use(IReconciliationMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        _middlewares.Add(middleware);
        return this;
    }

    public async Task ExecuteAsync(ReconciliationCycleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var index = 0;

        Task Next()
        {
            if (index < _middlewares.Count)
            {
                var middleware = _middlewares[index++];
                return middleware.InvokeAsync(context, Next);
            }

            return Task.CompletedTask;
        }

        await Next();
    }
}


