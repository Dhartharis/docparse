using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocParse.Modules.Billing.Application;

namespace DocParse.Modules.Billing.Infrastructure;

public sealed class StripeGateway(HttpClient http, StripeConfiguration configuration) : IPaymentGateway
{
    private static string Id(string value, string prefix)
    {
        if (!Regex.IsMatch(value, "^" + prefix + "[A-Za-z0-9_]{1,200}$")) throw new PaymentProviderFailure(false);
        return value;
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, Dictionary<string, string>? fields,
        string? idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.SecretKey);
        request.Headers.Add("Stripe-Version", StripeConfiguration.ApiVersion);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (fields is not null) request.Content = new FormUrlEncodedContent(fields);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new PaymentProviderFailure((int)response.StatusCode is 409 or 429 or >= 500);
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (output.Length + read > 1_048_576) throw new PaymentProviderFailure(false);
                output.Write(buffer, 0, read);
            }
            using var json = JsonDocument.Parse(output.ToArray());
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new PaymentProviderFailure(false);
            return json.RootElement.Clone();
        }
        catch (HttpRequestException) { throw new PaymentProviderFailure(true); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new PaymentProviderFailure(true); }
        catch (JsonException) { throw new PaymentProviderFailure(false); }
    }

    private static string Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()! : throw new PaymentProviderFailure(false);

    private static bool Number(JsonElement value, string name, out long number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) &&
            item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out number);
    }

    private static void TestMode(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("livemode", out var mode) || mode.ValueKind != JsonValueKind.False)
            throw new PaymentProviderFailure(false);
    }

    public async Task ValidatePriceAsync(string priceId, CancellationToken ct)
    {
        var price = await SendAsync(HttpMethod.Get, "v1/prices/" + Id(priceId, "price_"), null, null, ct);
        TestMode(price);
        if (Text(price, "id") != priceId || Text(price, "type") != "recurring" || Text(price, "currency") != "usd" ||
            !price.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.True ||
            !price.TryGetProperty("recurring", out var recurring) || Text(recurring, "interval") != "month" ||
            !Number(recurring, "interval_count", out var count) || count != 1 ||
            !Number(price, "unit_amount", out var cents) || cents <= 0)
            throw new PaymentProviderFailure(false);
    }

    public async Task<string> CreateCustomerAsync(Guid organizationId, CancellationToken ct)
    {
        var customer = await SendAsync(HttpMethod.Post, "v1/customers", new()
        {
            ["metadata[docparse_organization_id]"] = organizationId.ToString()
        }, "docparse-customer-" + organizationId, ct);
        TestMode(customer);
        if (!customer.TryGetProperty("metadata", out var metadata) || Text(metadata, "docparse_organization_id") != organizationId.ToString())
            throw new PaymentProviderFailure(false);
        return Id(Text(customer, "id"), "cus_");
    }

    public async Task<HostedCheckout> CreateCheckoutAsync(CheckoutClaim claim, string customerId, CancellationToken ct)
    {
        var checkout = await SendAsync(HttpMethod.Post, "v1/checkout/sessions", new()
        {
            ["mode"] = "subscription", ["customer"] = Id(customerId, "cus_"),
            ["client_reference_id"] = claim.OrganizationId.ToString(),
            ["line_items[0][price]"] = claim.PriceId, ["line_items[0][quantity]"] = "1",
            ["success_url"] = claim.ReturnBase + "/#billing=return", ["cancel_url"] = claim.ReturnBase + "/#billing=cancel",
            ["metadata[docparse_attempt_id]"] = claim.Id.ToString(),
            ["subscription_data[metadata][docparse_organization_id]"] = claim.OrganizationId.ToString(),
            ["subscription_data[metadata][docparse_plan_key]"] = claim.PlanKey
        }, "docparse-checkout-" + claim.Id, ct);
        return ParseCheckout(checkout, claim with { CustomerId = customerId });
    }

    public async Task<HostedCheckout> GetCheckoutAsync(CheckoutClaim claim, CancellationToken ct) =>
        ParseCheckout(await SendAsync(HttpMethod.Get, "v1/checkout/sessions/" + Id(claim.CheckoutId!, "cs_test_"), null, null, ct), claim);

    private static HostedCheckout ParseCheckout(JsonElement value, CheckoutClaim claim)
    {
        TestMode(value);
        var state = Text(value, "status");
        if (Text(value, "mode") != "subscription" || Text(value, "customer") != claim.CustomerId ||
            Text(value, "client_reference_id") != claim.OrganizationId.ToString() || state is not ("open" or "complete" or "expired") ||
            !Number(value, "expires_at", out var seconds) || seconds is < 0 or > 253402300799)
            throw new PaymentProviderFailure(false);
        var url = state == "open" ? SafeUrl(Text(value, "url"), "checkout.stripe.com") : "";
        return new(Id(Text(value, "id"), "cs_test_"), url, state, DateTimeOffset.FromUnixTimeSeconds(seconds));
    }

    public async Task<string> CreatePortalAsync(string customerId, string returnBase, CancellationToken ct)
    {
        var portal = await SendAsync(HttpMethod.Post, "v1/billing_portal/sessions", new()
        {
            ["customer"] = Id(customerId, "cus_"), ["return_url"] = returnBase + "/#billing=return"
        }, null, ct);
        TestMode(portal);
        if (Text(portal, "customer") != customerId) throw new PaymentProviderFailure(false);
        return SafeUrl(Text(portal, "url"), "billing.stripe.com");
    }

    private static string SafeUrl(string raw, string host)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != host ||
            uri.UserInfo.Length != 0 || !uri.IsDefaultPort)
            throw new PaymentProviderFailure(false);
        return uri.AbsoluteUri;
    }

    public async Task<PaymentSubscription?> GetCurrentSubscriptionAsync(string customerId, Guid organizationId, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Get, "v1/subscriptions?customer=" + Id(customerId, "cus_") + "&status=all&limit=100", null, null, ct);
        if (!result.TryGetProperty("has_more", out var more) || more.ValueKind != JsonValueKind.False ||
            !result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new PaymentProviderFailure(false);
        var subscriptions = new List<(PaymentSubscription Value, long Created)>();
        foreach (var item in data.EnumerateArray())
        {
            TestMode(item);
            if (Text(item, "customer") != customerId || !item.TryGetProperty("metadata", out var metadata) ||
                Text(metadata, "docparse_organization_id") != organizationId.ToString()) throw new PaymentProviderFailure(false);
            var plan = Text(metadata, "docparse_plan_key");
            if (!configuration.Billing.Prices.TryGetValue(plan, out var expectedPrice) ||
                !item.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object || !items.TryGetProperty("data", out var lines) || lines.ValueKind != JsonValueKind.Array ||
                lines.GetArrayLength() != 1 || lines[0].ValueKind != JsonValueKind.Object || !lines[0].TryGetProperty("price", out var price) || Text(price, "id") != expectedPrice ||
                !Number(item, "created", out var createdSeconds))
                throw new PaymentProviderFailure(false);
            var status = Text(item, "status");
            if (status is not ("incomplete" or "incomplete_expired" or "trialing" or "active" or "past_due" or "canceled" or "unpaid" or "paused"))
                throw new PaymentProviderFailure(false);
            subscriptions.Add((new(Id(Text(item, "id"), "sub_"), status, plan), createdSeconds));
        }
        var current = subscriptions.Where(item => item.Value.Status is not ("canceled" or "incomplete_expired")).ToArray();
        if (current.Length > 1) throw new PaymentProviderFailure(false);
        return current.Length == 1 ? current[0].Value : subscriptions.OrderByDescending(item => item.Created).ThenBy(item => item.Value.Id).FirstOrDefault().Value;
    }
}
