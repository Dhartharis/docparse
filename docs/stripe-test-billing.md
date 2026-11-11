# Stripe test billing

This increment implements test-mode Checkout, customer portal access, signed event ingestion and durable subscription-state reconciliation. It is disabled by default and runs without a Stripe account when disabled. The isolated integration test uses a local provider double; no Stripe credentials or real payments are needed for automated validation.

The payment boundary is `IPaymentGateway`, owned by Billing. The REST adapter pins API version `2026-10-28.endive`, using the existing .NET HTTP stack rather than adding an SDK. Application orchestration depends on provider/store ports. Module data is in the additive `017_billing_test_mode.sql` migration; existing migrations are unchanged.

## User flow

An organization Admin opens **Account -> Test payments**. Before configuration, the panel explains that payments are not connected. After configuration, it offers configured Starter/Pro test subscriptions, current provider status, synchronization progress/retry and the Stripe customer portal. Viewers cannot access billing management. Return links never grant a plan or reset credits; reopen the panel to read the authoritative status.

Only test subscriptions are supported here. The UI does not publish unapproved commercial prices: the selected configured price is shown by Stripe Checkout before the test confirmation. Test-mode subscriptions never modify Fyr Usage entitlements, consume a credit allowance or unlock product features.

## Configure after creating the Stripe account

1. Use Stripe's test environment. Create monthly USD recurring prices for Starter and Pro. Amounts at this stage are test configuration, not approved commercial tariffs. No Unlimited price is supported until excess-billing policy is implemented.
2. Put `STRIPE_ENABLED=true`, `STRIPE_SECRET_KEY=sk_test_...`, `STRIPE_WEBHOOK_SECRET=whsec_...`, `STRIPE_STARTER_PRICE_ID=price_...` and `STRIPE_PRO_PRICE_ID=price_...` in the ignored local `.env` or environment. Do not put keys into `appsettings.json`, browser code or Git. Set the webhook signing secret for the actual forwarding endpoint, not an API key.
3. Configure the webhook endpoint/API version as `2026-10-28.endive`. Subscribe to `checkout.session.completed`, `checkout.session.async_payment_succeeded`, `checkout.session.async_payment_failed`, `customer.subscription.created`, `customer.subscription.updated`, `customer.subscription.deleted`, `invoice.paid` and `invoice.payment_failed`. Local forwarding should target `http://127.0.0.1:5128/api/billing/stripe/webhook`; use Stripe CLI's local forwarding when configuring a local test environment.
4. Configure Stripe's test customer portal. This increment does not define proration, tax, failed-payment grace periods or cross-product billing policy. Do not enable live collection through portal settings.
5. Once this PR is integrated, build the local image, apply additive migrations and recreate the API from the repository root:

   ```powershell
   docker compose build api migrate
   docker compose run --rm migrate
   docker compose up -d api
   npm --prefix front run build
   ```

The API reads return links from the trusted `PublicWeb:BaseUrl`, not request input. Compose sets the local browser URL to `http://127.0.0.1:5173`. No arbitrary price IDs, amounts, customer IDs, subscription IDs or return URLs are accepted from the browser.

The adapter rejects `sk_live_` keys and any response/event with `livemode=true`. Enabling live payments requires a later explicit change and acceptance; a configuration toggle cannot bypass this guard. A loopback/host.docker.internal HTTP provider double is allowed only in Development through `Stripe:TestEndpoint`. Production provider requests use the fixed HTTPS Stripe origin and never follow redirects. Checkout/portal redirect destinations are restricted to their exact Stripe HTTPS hosts, server-side and client-side.

## Reliability and security

- One customer per organization, one in-progress checkout per organization and persisted request/operation IDs. A retry uses the same provider parameters and idempotency keys; concurrent requests cannot open another checkout. Completed sessions awaiting webhook reconciliation block another new checkout.
- Customer and checkout claims commit before external I/O. Completion checks the persisted lease identity and expiry; a stale claimant cannot write results. Individual HTTP calls time out after 10 seconds; the checkout lease lasts two minutes.
- An uncertain creation remains the same operation even after a malformed/permanent provider response. If no provider checkout ID was persisted and its operation is at least 23 hours old, automatic replay stops before Stripe's 24-hour minimum key-retention window. Likewise an unresolved customer creation older than 23 hours stops. Operator reconciliation is required; do not clear these rows or mint another operation without checking Stripe for the original object. Automated reconciliation of those uncertain creations is not implemented.
- Raw request bytes are checked against Stripe's HMAC signature with constant-time comparison and an inclusive 300-second past/future tolerance. API version, direct-account scope and test mode are validated. A durable inbox commits before HTTP acknowledgement. No raw event bodies, card data, payment URLs or credentials are stored in the inbox.
- Stripe event identity is deduplicated by ID and normalized customer/type, allowing non-identity delivery metadata to change. A contradictory customer/type is a conflict. Unhandled types are acknowledged without side effects. Unknown customers are ignored, not linked by arbitrary event metadata.
- Reconciliation fetches current provider subscription state rather than applying event arrival order. It checks customer, organization metadata, configured price and a single current subscription. It does not treat an active/trialing status as proof of paid entitlement. One durable worker lane serializes reconciliation; provider snapshots cannot overwrite each other out of order. No database transaction spans provider I/O.
- Worker attempts/leases/due times survive restarts. Retryable transport/409/429/5xx failures have five attempts total, with 10/30/120/300-second delays. Other provider rejection/malformed data stops automatically. Exhausted/permanent failures appear in the Admin panel and may be explicitly retried. Stripe Retry-After handling is not implemented in this increment; the bounded local schedule applies.
- Keep persisted Data Protection keys and normal identity configuration as required by the local V1. Test containers/databases are disposable; the script does not modify the main application's data or volumes.

## Validation

```powershell
dotnet build back/DocParse.csproj
python tests/check_backend_boundaries.py
dotnet run --project tests/BillingPolicyTests/BillingPolicyTests.csproj
npm --prefix front ci
npm --prefix front run build
npm --prefix front run lint
npm --prefix front test -- --workers=4
python tests/billing_integration.py --browser
python tests/backend_integration.py --browser
git diff --check
```

The package-free .NET policy runner controls the clock to check exact signature tolerance boundaries. PostgreSQL integration tests force fixture due times and verify exact delays, maximum attempts, permanent versus retryable errors, restart recovery, concurrent checkout, lost provider acknowledgement, tenant/Viewer denial, event deduplication and no credit grants. Browser checks exercise disabled/configured states, localized desktop/mobile layouts, redirect restrictions, recovery and a real API-to-local-provider portal journey. External Stripe pages are intercepted with local test content in browser tests.

Both GitHub workflows validate only; frontend publication still requires a manual dispatch on main. The billing workflow builds/checks policies without credentials or containers. The Docker/PostgreSQL suite runs locally and is not claimed as a GitHub-hosted integration check.

## Remaining before commercial operation

- Create/configure the Stripe test account and execute acceptance against real Stripe test APIs/CLI. Automated local doubles cannot prove that merchant account setup, payment methods, email delivery or Stripe configuration are correct.
- Finalize prices/credits, invoice/excess-usage mechanism, cycle alignment, upgrade/downgrade/proration, cancellation, tax, refunds, disputes and failed-payment policies. Implement and validate paid entitlement/cycle synchronization with Fyr Usage after those decisions. Test subscriptions currently do not change credits.
- Implement excess-consumption billing and real invoice/payment detail views as separate features. The portal delegates supported invoice/subscription management to Stripe; this app currently mirrors subscription status only.
- Approve operational recovery for uncertain provider creation and scaling of the single reconciliation lane before production volume requires it.
- Prepare production networking, secrets, monitoring, HTTPS and coordinated frontend/backend deployment. Nothing in this increment deploys, provisions a merchant account or makes live charges.

Official references: [Checkout creation](https://docs.stripe.com/api/checkout/sessions/create), [customer portal sessions](https://docs.stripe.com/api/customer_portal/sessions/create), [event verification](https://docs.stripe.com/events/manage-webhook-endpoints), [API versioning](https://docs.stripe.com/api/versioning).
