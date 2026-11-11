namespace DocParse.Modules.Integrations.Contracts;

public sealed record OperationalSource(Guid Id, Guid OrganizationId, Guid Revision, string SourceBusinessId,
    int PriceMaxAgeSeconds, int StockMaxAgeSeconds, int ClockSkewSeconds, bool Push);
public interface IConnectionCatalog
{
    Task<OperationalSource?> GetActiveAsync(Guid organizationId, Guid connectionId, CancellationToken ct);
}
