using DocParse.Modules.Billing.Application;
using Npgsql;
using NpgsqlTypes;

namespace DocParse.Modules.Billing.Infrastructure;

public sealed class BillingStore(IConfiguration configuration) : IBillingStore
{
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var db = new NpgsqlConnection(configuration["PostgreSQL:ConnectionString"]);
        await db.OpenAsync(ct);
        return db;
    }

    private static NpgsqlCommand Command(NpgsqlConnection db, string sql, params (string Name, object? Value)[] values)
    {
        var command = new NpgsqlCommand(sql, db);
        foreach (var (name, value) in values)
            command.Parameters.Add(value is null ? new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = DBNull.Value } : new NpgsqlParameter(name, value));
        return command;
    }

    public async Task<BillingSnapshot> SnapshotAsync(Guid org, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var command = Command(db, """
            SELECT a.subscription_status,a.plan_key,a.customer_id IS NOT NULL,
              (SELECT count(*)::int FROM billing.webhook_inbox w WHERE w.customer_id=a.customer_id AND w.state IN ('pending','processing')),
              (SELECT count(*)::int FROM billing.webhook_inbox w WHERE w.customer_id=a.customer_id AND w.state='failed'),
              (SELECT plan_key FROM billing.checkout_attempts c WHERE c.organization_id=a.organization_id AND c.state IN ('creating','pending'))
            FROM billing.accounts a WHERE organization_id=@org
            """, ("org", org));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetBoolean(2), reader.GetInt32(3), reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5))
            : new("none", null, false, 0, 0, null);
    }

    public async Task<CheckoutClaim> ClaimCheckoutAsync(Guid org, Guid requestId, string plan, string price, string returnBase, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var insert = Command(db, "INSERT INTO billing.accounts(organization_id) VALUES(@org) ON CONFLICT DO NOTHING", ("org", org)))
            await insert.ExecuteNonQueryAsync(ct);
        string? customer;
        DateTimeOffset accountCreated;
        await using (var account = Command(db, "SELECT customer_id,subscription_status,created_at FROM billing.accounts WHERE organization_id=@org FOR UPDATE", ("org", org)))
        await using (var reader = await account.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            customer = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (reader.GetString(1) is not ("none" or "canceled" or "incomplete_expired")) throw new BillingFailure(BillingFailureKind.Conflict);
            accountCreated = reader.GetFieldValue<DateTimeOffset>(2);
        }
        var lease = Guid.NewGuid();
        Guid id;
        string storedPrice, storedReturn;
        string? checkout;
        DateTimeOffset created;
        await using (var existing = Command(db, """
            SELECT id,plan_key,price_id,return_base,checkout_id,created_at,lease_until>now()
            FROM billing.checkout_attempts WHERE organization_id=@org AND state IN ('creating','pending') FOR UPDATE
            """, ("org", org)))
        await using (var reader = await existing.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                if (reader.GetString(1) != plan || (!reader.IsDBNull(6) && reader.GetBoolean(6)))
                    throw new BillingFailure(BillingFailureKind.Conflict);
                id = reader.GetGuid(0); storedPrice = reader.GetString(2); storedReturn = reader.GetString(3);
                checkout = reader.IsDBNull(4) ? null : reader.GetString(4);
                created = reader.GetFieldValue<DateTimeOffset>(5);
            }
            else { id = Guid.NewGuid(); storedPrice = price; storedReturn = returnBase; checkout = null; created = default; }
        }
        if (created == default)
        {
            await using var check = Command(db, """
                SELECT EXISTS(SELECT 1 FROM billing.checkout_attempts WHERE organization_id=@org AND request_id=@request)
                    OR (EXISTS(SELECT 1 FROM billing.checkout_attempts WHERE organization_id=@org AND state='complete')
                        AND EXISTS(SELECT 1 FROM billing.accounts WHERE organization_id=@org AND subscription_id IS NULL))
                """, ("org", org), ("request", requestId));
            if ((bool)(await check.ExecuteScalarAsync(ct))!) throw new BillingFailure(BillingFailureKind.Conflict);
            await using var insert = Command(db, """
                INSERT INTO billing.checkout_attempts(id,organization_id,request_id,plan_key,price_id,return_base,state)
                VALUES(@id,@org,@request,@plan,@price,@return,'creating') RETURNING created_at
                """, ("id", id), ("org", org), ("request", requestId), ("plan", plan), ("price", price), ("return", returnBase));
            created = new DateTimeOffset((DateTime)(await insert.ExecuteScalarAsync(ct))!);
        }
        await using (var claim = Command(db, "UPDATE billing.checkout_attempts SET lease_id=@lease,lease_until=now()+interval '2 minutes' WHERE id=@id", ("lease", lease), ("id", id)))
            await claim.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return new(id, org, plan, storedPrice, storedReturn, customer, checkout, lease, created, accountCreated);
    }

    private static async Task AssertLeaseAsync(NpgsqlConnection db, CheckoutClaim claim, CancellationToken ct)
    {
        await using var command = Command(db, "SELECT id FROM billing.checkout_attempts WHERE id=@id AND organization_id=@org AND lease_id=@lease AND lease_until>now() FOR UPDATE", ("id", claim.Id), ("org", claim.OrganizationId), ("lease", claim.LeaseId));
        if (await command.ExecuteScalarAsync(ct) is null) throw new BillingFailure(BillingFailureKind.Conflict);
    }

    public async Task SaveCustomerAsync(CheckoutClaim claim, string customerId, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        // Same account -> attempt lock order as ClaimCheckoutAsync.
        await using (var account = Command(db, "SELECT organization_id FROM billing.accounts WHERE organization_id=@org FOR UPDATE", ("org", claim.OrganizationId)))
            await account.ExecuteScalarAsync(ct);
        await AssertLeaseAsync(db, claim, ct);
        await using var command = Command(db, "UPDATE billing.accounts SET customer_id=@customer,updated_at=now() WHERE organization_id=@org AND (customer_id IS NULL OR customer_id=@customer)", ("org", claim.OrganizationId), ("customer", customerId));
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new BillingFailure(BillingFailureKind.Conflict);
        await tx.CommitAsync(ct);
    }

    public async Task SaveCheckoutAsync(CheckoutClaim claim, HostedCheckout checkout, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await AssertLeaseAsync(db, claim, ct);
        await using var command = Command(db, """
            UPDATE billing.checkout_attempts SET checkout_id=@checkout,expires_at=@expires,state=@state,lease_id=NULL,lease_until=NULL WHERE id=@id
            """, ("id", claim.Id), ("checkout", checkout.Id), ("expires", checkout.ExpiresAt), ("state", checkout.State == "open" ? "pending" : checkout.State));
        await command.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task ReleaseCheckoutAsync(CheckoutClaim claim, CancellationToken ct)
    {
        // A failed/malformed response can follow provider success. Preserve the same
        // operation and key even for permanent errors; never silently open a second checkout.
        await using var db = await OpenAsync(ct);
        await using var command = Command(db, "UPDATE billing.checkout_attempts SET lease_id=NULL,lease_until=NULL WHERE id=@id AND lease_id=@lease", ("id", claim.Id), ("lease", claim.LeaseId));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> CustomerAsync(Guid org, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var command = Command(db, "SELECT customer_id FROM billing.accounts WHERE organization_id=@org", ("org", org));
        return await command.ExecuteScalarAsync(ct) as string;
    }

    public async Task AcceptEventAsync(IncomingPaymentEvent notification, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var command = Command(db, """
            INSERT INTO billing.webhook_inbox(event_id,customer_id,event_type) VALUES(@id,@customer,@type)
            ON CONFLICT(event_id) DO UPDATE SET event_id=excluded.event_id
            WHERE webhook_inbox.customer_id=excluded.customer_id AND webhook_inbox.event_type=excluded.event_type
            RETURNING event_id
            """, ("id", notification.Id), ("customer", notification.CustomerId), ("type", notification.Type));
        if (await command.ExecuteScalarAsync(ct) is null) throw new BillingFailure(BillingFailureKind.Conflict);
    }

    public async Task<PaymentEventWork?> ClaimEventAsync(CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        // One durable reconciliation at a time prevents out-of-order API snapshots
        // from overwriting newer state. This deliberately starts with one worker lane.
        await using (var gate = Command(db, "SELECT pg_advisory_xact_lock(927361452)")) await gate.ExecuteNonQueryAsync(ct);
        await using (var expired = Command(db, "UPDATE billing.webhook_inbox SET state='failed',error_code='attempts_exhausted',lease_id=NULL,lease_until=NULL,updated_at=now() WHERE state='processing' AND lease_until<=now() AND attempts>=5"))
            await expired.ExecuteNonQueryAsync(ct);
        await using (var busy = Command(db, "SELECT EXISTS(SELECT 1 FROM billing.webhook_inbox WHERE state='processing' AND lease_until>now())"))
            if ((bool)(await busy.ExecuteScalarAsync(ct))!) return null;
        var lease = Guid.NewGuid();
        string eventId, customer;
        int attempts;
        await using (var claim = Command(db, """
            UPDATE billing.webhook_inbox SET state='processing',lease_id=@lease,lease_until=now()+interval '1 minute',attempts=attempts+1,updated_at=now()
            WHERE event_id=(SELECT event_id FROM billing.webhook_inbox
                WHERE attempts<5 AND ((state='pending' AND due_at<=now()) OR (state='processing' AND lease_until<=now()))
                ORDER BY created_at,event_id FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING event_id,customer_id,attempts
            """, ("lease", lease)))
        await using (var reader = await claim.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            eventId = reader.GetString(0); customer = reader.GetString(1); attempts = reader.GetInt32(2);
        }
        Guid org;
        await using (var account = Command(db, "SELECT organization_id FROM billing.accounts WHERE customer_id=@customer", ("customer", customer)))
        {
            var value = await account.ExecuteScalarAsync(ct);
            if (value is not Guid id)
            {
                await using var ignore = Command(db, "UPDATE billing.webhook_inbox SET state='ignored',lease_id=NULL,lease_until=NULL WHERE event_id=@id", ("id", eventId));
                await ignore.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
                return null;
            }
            org = id;
        }
        await tx.CommitAsync(ct);
        return new(eventId, customer, org, lease, attempts);
    }

    public async Task FinishEventAsync(PaymentEventWork work, PaymentSubscription? subscription, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var fence = Command(db, "SELECT event_id FROM billing.webhook_inbox WHERE event_id=@id AND lease_id=@lease AND state='processing' AND lease_until>now() FOR UPDATE", ("id", work.Id), ("lease", work.LeaseId)))
            if (await fence.ExecuteScalarAsync(ct) is null) return;
        await using (var update = Command(db, """
            UPDATE billing.accounts SET subscription_id=@subscription,subscription_status=@status,plan_key=@plan,updated_at=now()
            WHERE organization_id=@org AND customer_id=@customer
            """, ("subscription", subscription?.Id), ("status", subscription?.Status ?? "none"), ("plan", subscription?.PlanKey), ("org", work.OrganizationId), ("customer", work.CustomerId)))
            if (await update.ExecuteNonQueryAsync(ct) != 1) throw new BillingFailure(BillingFailureKind.Conflict);
        await using (var complete = Command(db, "UPDATE billing.webhook_inbox SET state='complete',lease_id=NULL,lease_until=NULL,error_code=NULL,updated_at=now() WHERE event_id=@id", ("id", work.Id)))
            await complete.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task FailEventAsync(PaymentEventWork work, bool retryable, CancellationToken ct)
    {
        var failed = !retryable || work.Attempts >= 5;
        var delays = new[] { 10, 30, 120, 300 };
        await using var db = await OpenAsync(ct);
        await using var command = Command(db, """
            UPDATE billing.webhook_inbox SET state=@state,error_code=@error,due_at=now()+make_interval(secs=>@delay),
            lease_id=NULL,lease_until=NULL,updated_at=now() WHERE event_id=@id AND lease_id=@lease AND state='processing' AND lease_until>now()
            """, ("state", failed ? "failed" : "pending"), ("error", retryable ? "provider_unavailable" : "provider_rejected"),
            ("delay", failed ? 0 : delays[work.Attempts - 1]), ("id", work.Id), ("lease", work.LeaseId));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RetryFailedAsync(Guid org, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var command = Command(db, """
            UPDATE billing.webhook_inbox SET state='pending',attempts=0,due_at=now(),error_code=NULL,updated_at=now()
            WHERE state='failed' AND customer_id=(SELECT customer_id FROM billing.accounts WHERE organization_id=@org)
            """, ("org", org));
        await command.ExecuteNonQueryAsync(ct);
    }
}
