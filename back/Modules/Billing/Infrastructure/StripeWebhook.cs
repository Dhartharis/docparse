using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocParse.Modules.Billing.Application;

namespace DocParse.Modules.Billing.Infrastructure;

public static class StripeWebhook
{
    private static readonly HashSet<string> Supported = [
        "checkout.session.completed", "checkout.session.async_payment_succeeded", "checkout.session.async_payment_failed",
        "customer.subscription.created", "customer.subscription.updated", "customer.subscription.deleted",
        "invoice.paid", "invoice.payment_failed"
    ];

    public static IncomingPaymentEvent? Verify(byte[] body, string signature, string secret, DateTimeOffset now)
    {
        try
        {
            if (body.Length > 65536 || signature.Length > 4096) throw Invalid();
            var parts = signature.Split(',').Select(part => part.Trim().Split('=', 2)).Where(part => part.Length == 2).ToArray();
            var times = parts.Where(part => part[0] == "t").Select(part => part[1]).ToArray();
            if (times.Length != 1 || !long.TryParse(times[0], NumberStyles.None, CultureInfo.InvariantCulture, out var time) ||
                Math.Abs((decimal)now.ToUnixTimeSeconds() - time) > 300) throw Invalid();
            var prefix = Encoding.UTF8.GetBytes(times[0] + ".");
            var input = new byte[prefix.Length + body.Length];
            prefix.CopyTo(input, 0); body.CopyTo(input, prefix.Length);
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), input);
            var verified = false;
            foreach (var part in parts.Where(part => part[0] == "v1" && part[1].Length == 64))
            {
                try { verified |= CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(part[1])); }
                catch (FormatException) { /* A malformed candidate is not a valid rotation signature. */ }
            }
            if (!verified) throw Invalid();
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("livemode").ValueKind != JsonValueKind.False ||
                root.GetProperty("api_version").GetString() != StripeConfiguration.ApiVersion ||
                (root.TryGetProperty("account", out var account) && account.ValueKind != JsonValueKind.Null)) throw Invalid();
            var id = root.GetProperty("id").GetString() ?? "";
            var type = root.GetProperty("type").GetString() ?? "";
            if (!Regex.IsMatch(id, "^evt_[A-Za-z0-9_]{1,200}$")) throw Invalid();
            if (!Supported.Contains(type)) return null;
            var customer = root.GetProperty("data").GetProperty("object").GetProperty("customer");
            var customerId = customer.ValueKind == JsonValueKind.Object ? customer.GetProperty("id").GetString() : customer.GetString();
            if (customerId is null) return null;
            if (!Regex.IsMatch(customerId, "^cus_[A-Za-z0-9_]{1,200}$")) throw Invalid();
            return new(id, type, customerId);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw Invalid(); }
    }

    private static BillingFailure Invalid() => new(BillingFailureKind.Invalid);
}
