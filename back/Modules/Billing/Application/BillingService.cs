namespace DocParse.Modules.Billing.Application;

public sealed class BillingService(IBillingStore store, IPaymentGateway gateway, BillingConfiguration configuration, TimeProvider clock)
{
    public async Task<HostedCheckout> CheckoutAsync(Guid org, Guid requestId, string plan, CancellationToken ct)
    {
        if (!configuration.Enabled) throw new BillingFailure(BillingFailureKind.Unavailable);
        if (requestId == Guid.Empty || !configuration.Prices.TryGetValue(plan, out var price))
            throw new BillingFailure(BillingFailureKind.Invalid);
        var claim = await store.ClaimCheckoutAsync(org, requestId, plan, price, configuration.ReturnBase, ct);
        try
        {
            if (claim.CustomerId is not null)
            {
                var current = await gateway.GetCurrentSubscriptionAsync(claim.CustomerId, org, ct);
                if (current is not null && current.Status is not ("canceled" or "incomplete_expired"))
                    throw new BillingFailure(BillingFailureKind.Conflict);
            }
            HostedCheckout checkout;
            if (claim.CheckoutId is not null)
            {
                checkout = await gateway.GetCheckoutAsync(claim, ct);
            }
            else
            {
                // Stripe retains idempotency keys for at least 24h. Fail closed before
                // that boundary if an acknowledged operation has not been persisted.
                if (clock.GetUtcNow() - claim.CreatedAt >= TimeSpan.FromHours(23) ||
                    (claim.CustomerId is null && clock.GetUtcNow() - claim.AccountCreatedAt >= TimeSpan.FromHours(23)))
                    throw new BillingFailure(BillingFailureKind.ReconciliationRequired);
                await gateway.ValidatePriceAsync(claim.PriceId, ct);
                var customer = claim.CustomerId ?? await gateway.CreateCustomerAsync(org, ct);
                await store.SaveCustomerAsync(claim, customer, ct);
                checkout = await gateway.CreateCheckoutAsync(claim, customer, ct);
            }
            await store.SaveCheckoutAsync(claim, checkout, ct);
            if (checkout.State != "open") throw new BillingFailure(BillingFailureKind.Conflict);
            return checkout;
        }
        catch (PaymentProviderFailure)
        {
            await store.ReleaseCheckoutAsync(claim, CancellationToken.None);
            throw new BillingFailure(BillingFailureKind.Unavailable);
        }
        catch
        {
            await store.ReleaseCheckoutAsync(claim, CancellationToken.None);
            throw;
        }
    }

    public async Task<string> PortalAsync(Guid org, CancellationToken ct)
    {
        if (!configuration.Enabled) throw new BillingFailure(BillingFailureKind.Unavailable);
        var customer = await store.CustomerAsync(org, ct) ?? throw new BillingFailure(BillingFailureKind.Conflict);
        try { return await gateway.CreatePortalAsync(customer, configuration.ReturnBase, ct); }
        catch (PaymentProviderFailure) { throw new BillingFailure(BillingFailureKind.Unavailable); }
    }
}
