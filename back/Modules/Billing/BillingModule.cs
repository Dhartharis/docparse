using DocParse.Modules.Access.Contracts;
using DocParse.Modules.Billing.Application;
using DocParse.Modules.Billing.Infrastructure;

namespace DocParse.Modules.Billing;

public static class BillingModule
{
    public static IServiceCollection AddBillingModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var stripe = new StripeConfiguration(configuration, environment);
        services.AddSingleton(stripe);
        services.AddSingleton(stripe.Billing);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IBillingStore, BillingStore>();
        services.AddScoped<BillingService>();
        services.AddHttpClient<IPaymentGateway, StripeGateway>(client =>
        {
            client.BaseAddress = stripe.Endpoint;
            client.Timeout = TimeSpan.FromSeconds(10);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHostedService<BillingReconciliationWorker>();
        return services;
    }

    public static void MapBillingModule(this WebApplication app)
    {
        var group = app.MapGroup("/api/organizations/{organizationId:guid}/billing").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            var org = Guid.Parse(context.HttpContext.Request.RouteValues["organizationId"]!.ToString()!);
            var access = context.HttpContext.RequestServices.GetRequiredService<IOrganizationAccess>();
            if (!await access.HasAccessAsync(org, true, context.HttpContext.RequestAborted)) return Results.NotFound();
            try { return await next(context); }
            catch (BillingFailure failure) { return Problem(failure); }
        });
        group.MapGet("/", async (Guid organizationId, IBillingStore store, BillingConfiguration config, CancellationToken ct) =>
            Results.Ok(new { enabled = config.Enabled, mode = "test", availablePlans = config.Prices.Keys.Order(),
                state = config.Enabled ? await store.SnapshotAsync(organizationId, ct) : new BillingSnapshot("none", null, false, 0, 0, null) }));
        group.MapPost("/checkout", async (Guid organizationId, CheckoutRequest request, BillingService billing, CancellationToken ct) =>
            Results.Ok(await billing.CheckoutAsync(organizationId, request.RequestId, request.PlanKey ?? "", ct))).RequireRateLimiting("authentication");
        group.MapPost("/portal", async (Guid organizationId, BillingService billing, CancellationToken ct) =>
            Results.Ok(new { url = await billing.PortalAsync(organizationId, ct) })).RequireRateLimiting("authentication");
        group.MapPost("/retry", async (Guid organizationId, IBillingStore store, BillingConfiguration config, CancellationToken ct) =>
        {
            if (!config.Enabled) throw new BillingFailure(BillingFailureKind.Unavailable);
            await store.RetryFailedAsync(organizationId, ct);
            return Results.NoContent();
        }).RequireRateLimiting("authentication");

        app.MapPost("/api/billing/stripe/webhook", async (HttpContext context, StripeConfiguration config, IBillingStore store, TimeProvider clock) =>
        {
            if (!config.Enabled) return Results.NotFound();
            try
            {
                if (context.Request.ContentType?.Split(';')[0].Trim() != "application/json") return Results.StatusCode(415);
                using var body = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    if (body.Length + read > 65536) return Results.StatusCode(413);
                    body.Write(buffer, 0, read);
                }
                var notification = StripeWebhook.Verify(body.ToArray(), context.Request.Headers["Stripe-Signature"].ToString(), config.WebhookSecret, clock.GetUtcNow());
                if (notification is not null) await store.AcceptEventAsync(notification, context.RequestAborted);
                return Results.Ok();
            }
            catch (BillingFailure failure) { return Problem(failure); }
        }).AllowAnonymous();
    }

    private static IResult Problem(BillingFailure failure)
    {
        var status = failure.Kind switch { BillingFailureKind.Invalid => 400, BillingFailureKind.Conflict => 409, _ => 503 };
        var code = failure.Kind switch { BillingFailureKind.Invalid => "validation", BillingFailureKind.Conflict => "billing_conflict",
            BillingFailureKind.ReconciliationRequired => "billing_reconciliation", _ => "billing_unavailable" };
        return Results.Problem(statusCode: status, title: "The test billing operation could not be completed.",
            extensions: new Dictionary<string, object?> { ["code"] = code });
    }

    private sealed record CheckoutRequest(Guid RequestId, string? PlanKey);
}
