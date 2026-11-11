using System.Data.Common;

namespace DocParse.Modules.OperationalData.Contracts;

public sealed record OperationalPrice(bool Known, string? Reason, string? Amount, string? Currency, string? TaxTreatment,
    DateTimeOffset? ObservedAt, DateTimeOffset? ValidUntil);
public sealed record OperationalQuantity(bool Known, string? Reason, string? Quantity);
public sealed record OperationalAvailability(string Tracking, OperationalQuantity Available, OperationalQuantity Reserved,
    DateTimeOffset ObservedAt, DateTimeOffset ValidUntil);
public sealed record OperationalOffer(string ExternalId, string ProductId, string? Sku, string Name, string Presentation,
    string UnitCode, string Version, DateTimeOffset UpdatedAt, bool Active, OperationalPrice Price, OperationalAvailability Availability);
public sealed record OperationalTombstone(string ExternalId, string Version, DateTimeOffset DeletedAt);
public sealed record OperationalChange(OperationalOffer? Offer, OperationalTombstone? Tombstone);
public enum ProjectionOutcome { Applied, IgnoredOlderVersion, VersionConflict }
public sealed class OperationalDataInvalid : Exception;
public sealed record OperationalRecordView(OperationalOffer Offer, bool PriceCurrent, bool AvailabilityCurrent);
public sealed record OperationalSnapshot(long Total, IReadOnlyList<OperationalRecordView> Records);

public interface IOperationalProjection
{
    void Validate(OperationalChange change, DateTimeOffset now, int clockSkewSeconds);
    // The caller owns the short local transaction so its inbox outcome commits with the projection.
    Task<ProjectionOutcome> ApplyAsync(Guid organizationId, Guid connectionId, OperationalChange change, DateTimeOffset now,
        int clockSkewSeconds, DbTransaction transaction, CancellationToken ct);
    Task<OperationalSnapshot> ReadAsync(Guid organizationId, Guid connectionId, int priceMaxAgeSeconds, int stockMaxAgeSeconds,
        DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<OperationalRecordView>> CheckLiveAsync(Guid organizationId, Guid connectionId, IReadOnlyList<OperationalOffer> offers,
        int priceMaxAgeSeconds, int stockMaxAgeSeconds, DateTimeOffset now, CancellationToken ct);
}
