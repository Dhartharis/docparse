using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocParse.Modules.Integrations.Application;
using DocParse.Modules.OperationalData.Contracts;

namespace DocParse.Modules.Integrations.Infrastructure;

public static class OperationalEventCodec
{
    internal static void Shape(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !names.Contains(property.Name, StringComparer.Ordinal)) throw Invalid();
        if (seen.Count != names.Length) throw Invalid();
    }
    internal static string Text(JsonElement value, string property) => value.GetProperty(property).ValueKind == JsonValueKind.String
        ? value.GetProperty(property).GetString()! : throw Invalid();
    internal static DateTimeOffset Instant(JsonElement value, string property)
    {
        var text = Text(value, property);
        if (!Regex.IsMatch(text, @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?Z\z") ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)) throw Invalid();
        return time;
    }
    private static OperationalPrice Price(JsonElement value)
    {
        var known = Text(value, "state") == "known";
        if (!known)
        {
            Shape(value, "state", "reason");
            if (Text(value, "state") != "unknown") throw Invalid();
            return new(false, Text(value, "reason"), null, null, null, null, null);
        }
        Shape(value, "state", "amount", "currency", "taxTreatment", "observedAt", "validUntil");
        return new(true, null, Text(value, "amount"), Text(value, "currency"), Text(value, "taxTreatment"), Instant(value, "observedAt"), Instant(value, "validUntil"));
    }
    private static OperationalQuantity Quantity(JsonElement value)
    {
        if (Text(value, "state") == "known") { Shape(value, "state", "quantity"); return new(true, null, Text(value, "quantity")); }
        Shape(value, "state", "reason");
        if (Text(value, "state") != "unknown") throw Invalid();
        return new(false, Text(value, "reason"), null);
    }
    internal static OperationalOffer ReadOffer(JsonElement offer)
    {
        Shape(offer, "externalId", "productId", "sku", "name", "presentation", "unitCode", "version", "updatedAt", "active", "price", "availability");
        var stock = offer.GetProperty("availability");
        Shape(stock, "tracking", "available", "reserved", "observedAt", "validUntil");
        var active = offer.GetProperty("active");
        if (active.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
        return new(Text(offer, "externalId"), Text(offer, "productId"), offer.GetProperty("sku").ValueKind == JsonValueKind.Null ? null : Text(offer, "sku"),
            Text(offer, "name"), Text(offer, "presentation"), Text(offer, "unitCode"), Text(offer, "version"), Instant(offer, "updatedAt"), active.GetBoolean(),
            Price(offer.GetProperty("price")), new(Text(stock, "tracking"), Quantity(stock.GetProperty("available")), Quantity(stock.GetProperty("reserved")),
                Instant(stock, "observedAt"), Instant(stock, "validUntil")));
    }
    public static PushEvent Parse(byte[] body)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetString(body);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Shape(root, "contractVersion", "sourceBusinessId", "eventId", "occurredAt", "change");
            if (Text(root, "contractVersion") != "1" || !Guid.TryParseExact(Text(root, "eventId"), "D", out var eventId)) throw Invalid();
            var change = root.GetProperty("change");
            OperationalChange parsed;
            if (Text(change, "kind") == "delete")
            {
                Shape(change, "kind", "tombstone");
                var tombstone = change.GetProperty("tombstone");
                Shape(tombstone, "externalId", "version", "deletedAt");
                parsed = new(null, new(Text(tombstone, "externalId"), Text(tombstone, "version"), Instant(tombstone, "deletedAt")));
            }
            else
            {
                if (Text(change, "kind") != "upsert") throw Invalid();
                Shape(change, "kind", "offer");
                parsed = new(ReadOffer(change.GetProperty("offer")), null);
            }
            var business = Text(root, "sourceBusinessId");
            if (!Regex.IsMatch(business, @"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z")) throw Invalid();
            return new(eventId, business, Instant(root, "occurredAt"), parsed, JsonSerializer.Serialize(root));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or DecoderFallbackException)
        { throw Invalid(); }
    }
    private static PushRejected Invalid() => new(400, "validation");
}
