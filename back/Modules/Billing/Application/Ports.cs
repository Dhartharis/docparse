namespace DocParse.Modules.Billing.Application;

public enum BillingFailureKind { Invalid, Conflict, Unavailable, ReconciliationRequired }
public sealed class BillingFailure(BillingFailureKind kind) : Exception("Billing could not complete the operation.")
{
    public BillingFailureKind Kind { get; } = kind;
}

public sealed class PaymentProviderFailure(bool retryable) : Exception("The payment provider request failed.")
{
    public bool Retryable { get; } = retryable;
}

public sealed record BillingConfiguration(bool Enabled, string ReturnBase, IReadOnlyDictionary<string, string> Prices);
public sealed record HostedCheckout(string Id, string Url, string State, DateTimeOffset ExpiresAt);
public sealed record PaymentSubscription(string Id, string Status, string PlanKey);
public sealed record CheckoutClaim(Guid Id, Guid OrganizationId, string PlanKey, string PriceId, string ReturnBase,
    string? CustomerId, string? CheckoutId, Guid LeaseId, DateTimeOffset CreatedAt, DateTimeOffset AccountCreatedAt);
public sealed record IncomingPaymentEvent(string Id, string Type, string CustomerId);
public sealed record PaymentEventWork(string Id, string CustomerId, Guid OrganizationId, Guid LeaseId, int Attempts);
public sealed record BillingSnapshot(string Status, string? PlanKey, bool HasCustomer, int PendingEvents, int FailedEvents, string? CheckoutPlanKey);

public interface IPaymentGateway
{
    Task ValidatePriceAsync(string priceId, CancellationToken ct);
    Task<string> CreateCustomerAsync(Guid organizationId, CancellationToken ct);
    Task<HostedCheckout> CreateCheckoutAsync(CheckoutClaim claim, string customerId, CancellationToken ct);
    Task<HostedCheckout> GetCheckoutAsync(CheckoutClaim claim, CancellationToken ct);
    Task<string> CreatePortalAsync(string customerId, string returnBase, CancellationToken ct);
    Task<PaymentSubscription?> GetCurrentSubscriptionAsync(string customerId, Guid organizationId, CancellationToken ct);
}

public interface IBillingStore
{
    Task<BillingSnapshot> SnapshotAsync(Guid org, CancellationToken ct);
    Task<CheckoutClaim> ClaimCheckoutAsync(Guid org, Guid requestId, string plan, string price, string returnBase, CancellationToken ct);
    Task SaveCustomerAsync(CheckoutClaim claim, string customerId, CancellationToken ct);
    Task SaveCheckoutAsync(CheckoutClaim claim, HostedCheckout checkout, CancellationToken ct);
    Task ReleaseCheckoutAsync(CheckoutClaim claim, CancellationToken ct);
    Task<string?> CustomerAsync(Guid org, CancellationToken ct);
    Task AcceptEventAsync(IncomingPaymentEvent notification, CancellationToken ct);
    Task<PaymentEventWork?> ClaimEventAsync(CancellationToken ct);
    Task FinishEventAsync(PaymentEventWork work, PaymentSubscription? subscription, CancellationToken ct);
    Task FailEventAsync(PaymentEventWork work, bool retryable, CancellationToken ct);
    Task RetryFailedAsync(Guid org, CancellationToken ct);
}
