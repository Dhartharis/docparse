using System.Numerics;
using DocParse.Modules.OperationalData.Contracts;

namespace DocParse.Modules.OperationalData.Application;

public static class LiveEvidence
{
    public static void Check(OperationalOffer live, string version, bool deleted, OperationalOffer? stored)
    {
        var comparison = BigInteger.Parse(live.Version,System.Globalization.CultureInfo.InvariantCulture)
            .CompareTo(BigInteger.Parse(version,System.Globalization.CultureInfo.InvariantCulture));
        if (comparison < 0 || deleted && comparison == 0) throw new OperationalDataInvalid();
        if (comparison != 0 || stored is null) return;
        if (live.ProductId != stored.ProductId || live.Sku != stored.Sku || live.Name != stored.Name || live.Presentation != stored.Presentation ||
            live.UnitCode != stored.UnitCode || live.Active != stored.Active ||
            live.Price.Known && stored.Price.Known && (live.Price.Amount != stored.Price.Amount || live.Price.Currency != stored.Price.Currency || live.Price.TaxTreatment != stored.Price.TaxTreatment) ||
            live.Availability.Tracking != "unknown" && stored.Availability.Tracking != "unknown" && live.Availability.Tracking != stored.Availability.Tracking ||
            live.Availability.Available.Known && stored.Availability.Available.Known && live.Availability.Available.Quantity != stored.Availability.Available.Quantity ||
            live.Availability.Reserved.Known && stored.Availability.Reserved.Known && live.Availability.Reserved.Quantity != stored.Availability.Reserved.Quantity)
            throw new OperationalDataInvalid();
    }
    public static OperationalRecordView View(OperationalOffer offer, int priceAge, int stockAge, DateTimeOffset now) => new(offer,
        offer.Active && offer.Price.Known && now < offer.Price.ValidUntil && now < offer.Price.ObservedAt?.AddSeconds(priceAge),
        offer.Active && offer.Availability.Tracking == "tracked" && now < offer.Availability.ValidUntil && now < offer.Availability.ObservedAt.AddSeconds(stockAge));
}
