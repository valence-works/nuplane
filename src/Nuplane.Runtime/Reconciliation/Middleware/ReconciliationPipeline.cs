namespace Nuplane.Runtime.Reconciliation.Middleware;

internal sealed class ReconciliationPipeline
{
    private readonly List<IReconciliationMiddleware> middlewares = [];

    public ReconciliationPipeline Use(IReconciliationMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        middlewares.Add(middleware);
        return this;
    }

    public async Task ExecuteAsync(ReconciliationCycleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var index = 0;

        Task Next()
        {
            if (index < middlewares.Count)
            {
                var middleware = middlewares[index++];
                return middleware.InvokeAsync(context, Next);
            }

            return Task.CompletedTask;
        }

        await Next();
    }
}


