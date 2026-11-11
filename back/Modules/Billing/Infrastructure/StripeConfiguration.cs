using DocParse.Modules.Billing.Application;

namespace DocParse.Modules.Billing.Infrastructure;

public sealed class StripeConfiguration
{
    public const string ApiVersion = "2026-10-28.endive";
    public bool Enabled { get; }
    public string SecretKey { get; }
    public string WebhookSecret { get; }
    public Uri Endpoint { get; }
    public BillingConfiguration Billing { get; }

    public StripeConfiguration(IConfiguration config, IHostEnvironment environment)
    {
        Enabled = config.GetValue<bool>("Stripe:Enabled");
        SecretKey = config["Stripe:SecretKey"] ?? "";
        WebhookSecret = config["Stripe:WebhookSecret"] ?? "";
        Endpoint = new Uri(config["Stripe:TestEndpoint"] ?? "https://api.stripe.com/");
        var site = config["PublicWeb:BaseUrl"] ?? "http://127.0.0.1:5173";
        var prices = new Dictionary<string, string>();
        foreach (var plan in new[] { "starter", "pro" })
            if (config[$"Stripe:Prices:{plan}"] is string price && !string.IsNullOrWhiteSpace(price))
                prices.Add(plan, price);
        Billing = new(Enabled, site.TrimEnd('/'), prices);
        if (!Enabled) return;
        if (!SecretKey.StartsWith("sk_test_", StringComparison.Ordinal) || !WebhookSecret.StartsWith("whsec_", StringComparison.Ordinal))
            throw new InvalidOperationException("Stripe test credentials and a webhook signing secret are required. Live billing is not supported.");
        var official = Endpoint.AbsoluteUri == "https://api.stripe.com/";
        var localDouble = environment.IsDevelopment() && Endpoint.Scheme == "http" &&
            (Endpoint.IsLoopback || Endpoint.Host == "host.docker.internal") && string.IsNullOrEmpty(Endpoint.UserInfo) && Endpoint.AbsolutePath == "/";
        if (!official && !localDouble) throw new InvalidOperationException("Only Stripe or a local Development payment double is allowed.");
        if (!Uri.TryCreate(site, UriKind.Absolute, out var web) || web.UserInfo.Length != 0 || web.Query.Length != 0 || web.Fragment.Length != 0 ||
            (web.Scheme != "https" && !(environment.IsDevelopment() && web.Scheme == "http" && web.IsLoopback)))
            throw new InvalidOperationException("Configure a trusted public web URL for billing return links.");
        if (prices.Values.Any(price => !System.Text.RegularExpressions.Regex.IsMatch(price, "^price_[A-Za-z0-9_]{1,200}$")))
            throw new InvalidOperationException("Configure Stripe Price IDs, never client-supplied amounts.");
    }
}
