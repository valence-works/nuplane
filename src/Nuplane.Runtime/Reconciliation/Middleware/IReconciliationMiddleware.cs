namespace Nuplane.Runtime.Reconciliation.Middleware;

internal interface IReconciliationMiddleware
{
    Task InvokeAsync(ReconciliationCycleContext context, Func<Task> next);
}


