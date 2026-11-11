using System.Text;
using System.Text.Json;
using DocParse.Modules.Integrations.Application;
using DocParse.Modules.Integrations.Contracts;
using DocParse.Modules.OperationalData.Contracts;
using static DocParse.Modules.Integrations.Infrastructure.OperationalEventCodec;

namespace DocParse.Modules.Integrations.Infrastructure;

public sealed record ProviderQueryResult(string SelectorValue, string Status, IReadOnlyList<OperationalOffer> Offers);
public static class LiveResponseCodec
{
    public static IReadOnlyList<ProviderQueryResult> Parse(byte[] bytes, string business, LiveQuery query, IOperationalProjection projection, DateTimeOffset now, int skew)
    {
        try
        {
            _ = new UTF8Encoding(false,true).GetString(bytes);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            Shape(root,"contractVersion","sourceBusinessId","results");
            if (Text(root,"contractVersion") != "1" || Text(root,"sourceBusinessId") != business) throw Invalid();
            var results = root.GetProperty("results");
            if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() != query.Values.Length) throw Invalid();
            var parsed = new List<ProviderQueryResult>();
            var index = 0;
            foreach (var result in results.EnumerateArray())
            {
                Shape(result,"selectorValue","status","offers");
                var selector = Text(result,"selectorValue"); var status = Text(result,"status");
                var offers = result.GetProperty("offers");
                if (selector != query.Values[index++] || offers.ValueKind != JsonValueKind.Array) throw Invalid();
                var count = offers.GetArrayLength();
                if (status switch { "found" => count != 1, "ambiguous" => count is < 2 or > 10, "not_found" or "unavailable" => count != 0, _ => true }) throw Invalid();
                var records = new List<OperationalOffer>();
                foreach (var value in offers.EnumerateArray())
                {
                    var offer = ReadOffer(value);
                    projection.Validate(new(offer,null),now,skew);
                    if (query.SelectorKind == "externalIds" && offer.ExternalId != selector || query.SelectorKind == "skus" && offer.Sku != selector) throw Invalid();
                    if (!query.Fields.Contains("price") && (offer.Price.Known || offer.Price.Reason != "not_requested")) throw Invalid();
                    if (!query.Fields.Contains("availability") && (offer.Availability.Tracking != "unknown" ||
                        offer.Availability.Available.Known || offer.Availability.Reserved.Known ||
                        offer.Availability.Available.Reason != "not_requested" || offer.Availability.Reserved.Reason != "not_requested")) throw Invalid();
                    if (records.Any(item => item.ExternalId == offer.ExternalId)) throw Invalid();
                    records.Add(offer);
                }
                parsed.Add(new(selector,status,records));
            }
            return parsed;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or DecoderFallbackException or PushRejected or OperationalDataInvalid)
        { throw Invalid(); }
    }
    private static LiveQueryFailure Invalid() => new("provider_response");
}
