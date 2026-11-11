using DocParse.Modules.Billing.Application;

namespace DocParse.Modules.Billing.Infrastructure;

public sealed class BillingReconciliationWorker(IServiceScopeFactory scopes, StripeConfiguration configuration,
    ILogger<BillingReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            PaymentEventWork? work = null;
            using var scope = scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IBillingStore>();
            try
            {
                work = await store.ClaimEventAsync(stoppingToken);
                if (work is not null)
                {
                    var gateway = scope.ServiceProvider.GetRequiredService<IPaymentGateway>();
                    var subscription = await gateway.GetCurrentSubscriptionAsync(work.CustomerId, work.OrganizationId, stoppingToken);
                    await store.FinishEventAsync(work, subscription, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning("Test billing reconciliation failed ({FailureType}).", error.GetType().Name);
                if (work is not null)
                {
                    try { await store.FailEventAsync(work, error is not PaymentProviderFailure provider || provider.Retryable, stoppingToken); }
                    catch (Exception saveError) when (!stoppingToken.IsCancellationRequested)
                    { logger.LogWarning("Test billing retry persistence failed ({FailureType}).", saveError.GetType().Name); }
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
