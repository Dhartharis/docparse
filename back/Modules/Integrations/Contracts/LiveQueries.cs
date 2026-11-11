using DocParse.Modules.OperationalData.Contracts;

namespace DocParse.Modules.Integrations.Contracts;

public sealed record LiveQuery(string[] Fields, string SelectorKind, string[] Values);
public sealed record LiveQueryResult(string SelectorValue, string Status, IReadOnlyList<OperationalRecordView> Records);
public sealed record LiveQueryResponse(DateTimeOffset QueriedAt, IReadOnlyList<LiveQueryResult> Results);
public interface ILiveQueries
{
    Task<LiveQueryResponse> QueryAsync(Guid organizationId, Guid connectionId, LiveQuery query, CancellationToken ct);
}
