using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocParse.Modules.Billing.Application;
using DocParse.Modules.Billing.Infrastructure;

// Package-free policy checks called from CI; no server, database or Stripe account.
var now = new DateTimeOffset(2026, 11, 11, 2, 0, 0, TimeSpan.Zero);
const string secret = "whsec_public_policy_fixture_only";
var count = 0;
byte[] Body(bool live = false, string version = StripeConfiguration.ApiVersion, string? account = null, string type = "invoice.paid") =>
    JsonSerializer.SerializeToUtf8Bytes(new { id = "evt_policy_fixture", type, livemode = live, api_version = version, account, data = new { @object = new { customer = "cus_policy_fixture" } } });
string Signature(byte[] body, long time)
{
    var prefix = Encoding.UTF8.GetBytes(time + ".");
    var input = new byte[prefix.Length + body.Length];
    prefix.CopyTo(input, 0); body.CopyTo(input, prefix.Length);
    return "t=" + time + ",v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), input)).ToLowerInvariant();
}
void Reject(Action action)
{
    try { action(); }
    catch (BillingFailure failure) when (failure.Kind == BillingFailureKind.Invalid) { count++; return; }
    throw new Exception("Expected invalid webhook rejection.");
}
var body = Body();
foreach (var offset in new[] { -300, 0, 300 })
{
    var value = StripeWebhook.Verify(body, Signature(body, now.ToUnixTimeSeconds() + offset), secret, now);
    if (value?.CustomerId != "cus_policy_fixture") throw new Exception("Expected valid webhook at tolerance boundary.");
    count++;
}
foreach (var offset in new[] { -301, 301 })
    Reject(() => StripeWebhook.Verify(body, Signature(body, now.ToUnixTimeSeconds() + offset), secret, now));
Reject(() => StripeWebhook.Verify(body, Signature(body, now.ToUnixTimeSeconds()), "wrong_secret", now));
Reject(() => StripeWebhook.Verify(body.Concat(new byte[] { 32 }).ToArray(), Signature(body, now.ToUnixTimeSeconds()), secret, now));
Reject(() => StripeWebhook.Verify(body, Signature(body, now.ToUnixTimeSeconds()) + ",t=" + now.ToUnixTimeSeconds(), secret, now));
foreach (var invalid in new[] { Body(live: true), Body(version: "2020-08-27"), Body(account: "acct_external"), Encoding.UTF8.GetBytes("{broken"), new byte[65537] })
    Reject(() => StripeWebhook.Verify(invalid, Signature(invalid, now.ToUnixTimeSeconds()), secret, now));
var rotated = StripeWebhook.Verify(body, Signature(body, now.ToUnixTimeSeconds()) + ",v1=" + new string('0', 64), secret, now);
if (rotated is null) throw new Exception("A valid signature candidate must survive rotation overlap.");
count++;
var ignored = Body(type: "unhandled.fixture");
if (StripeWebhook.Verify(ignored, Signature(ignored, now.ToUnixTimeSeconds()), secret, now) is not null) throw new Exception("Unsupported events must not enter reconciliation.");
count++;
Console.WriteLine($"PASS: {count} deterministic webhook policy checks, including exact clock-tolerance boundaries.");
