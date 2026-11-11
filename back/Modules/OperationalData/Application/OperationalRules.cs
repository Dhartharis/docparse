using System.Text.RegularExpressions;
using System.Text;
using DocParse.Modules.OperationalData.Contracts;

namespace DocParse.Modules.OperationalData.Application;

public static class OperationalRules
{
    private static readonly HashSet<string> Reasons = ["not_provided", "source_unavailable", "not_authorized", "not_requested", "not_tracked"];
    private static bool Match(string? value, string pattern) => value is not null && Regex.IsMatch(value, pattern);
    private static bool Id(string? value) => Match(value, @"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z");
    private static bool Version(string? value) => Match(value, @"\A[1-9][0-9]{0,37}\z");
    private static bool Decimal(string? value) => Match(value, @"\A(0|[1-9][0-9]{0,17})(\.[0-9]{1,6})?\z");
    private static bool Text(string? value, int maximum) => value is { Length: > 0 } && value.EnumerateRunes().Count() <= maximum && !value.Contains('\0');
    private static bool Instant(DateTimeOffset value, DateTimeOffset now, int skew) => value.Offset == TimeSpan.Zero && value <= now.AddSeconds(skew);
    private static bool Quantity(OperationalQuantity? value) => value is not null && (value.Known
        ? value.Reason is null && Decimal(value.Quantity) : value.Quantity is null && value.Reason is not null && Reasons.Contains(value.Reason));

    public static void Validate(OperationalChange change, DateTimeOffset now, int skew)
    {
        if (skew is < 0 or > 300 || (change.Offer is null) == (change.Tombstone is null)) throw new OperationalDataInvalid();
        if (change.Tombstone is { } tombstone)
        {
            if (!Id(tombstone.ExternalId) || !Version(tombstone.Version) || !Instant(tombstone.DeletedAt, now, skew)) throw new OperationalDataInvalid();
            return;
        }
        var offer = change.Offer!;
        if (!Id(offer.ExternalId) || !Id(offer.ProductId) || !Version(offer.Version) || !Text(offer.Name, 512) || !Text(offer.Presentation, 256) ||
            (offer.Sku is not null && !Text(offer.Sku, 128)) || !Match(offer.UnitCode, @"\A[A-Za-z][A-Za-z0-9._-]{0,31}\z") || !Instant(offer.UpdatedAt, now, skew))
            throw new OperationalDataInvalid();
        var price = offer.Price;
        if (price is null || (price.Known
            ? !Decimal(price.Amount) || !Match(price.Currency, @"\A[A-Z]{3}\z") || price.TaxTreatment is not ("inclusive" or "exclusive" or "unspecified") ||
                price.Reason is not null || price.ObservedAt is null || price.ValidUntil is null || !Instant(price.ObservedAt.Value, now, skew) ||
                price.ValidUntil.Value.Offset != TimeSpan.Zero || price.ValidUntil < price.ObservedAt
            : price.Reason is null || !Reasons.Contains(price.Reason) || price.Amount is not null || price.Currency is not null ||
                price.TaxTreatment is not null || price.ObservedAt is not null || price.ValidUntil is not null)) throw new OperationalDataInvalid();
        var stock = offer.Availability;
        if (stock is null || stock.Tracking is not ("tracked" or "untracked" or "unknown") || !Quantity(stock.Available) || !Quantity(stock.Reserved) ||
            (stock.Tracking != "tracked" && (stock.Available.Known || stock.Reserved.Known)) || !Instant(stock.ObservedAt, now, skew) ||
            stock.ValidUntil.Offset != TimeSpan.Zero || stock.ValidUntil < stock.ObservedAt) throw new OperationalDataInvalid();
    }
}
